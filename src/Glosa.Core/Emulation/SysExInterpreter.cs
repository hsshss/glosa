namespace Glosa.Core.Emulation;

/// <summary>
/// Decodes the SysEx a module would act on, updates the emulated panel, and decides what
/// reaches the device.
/// </summary>
/// <remarks>
/// Several GS parameters are also re-emitted as ordinary controllers when the connected
/// module cannot understand the exclusive form.
/// </remarks>
internal sealed class SysExInterpreter(EmulationFilter filter)
{
    /// <summary>GS part block to MIDI channel; the inverse of the channel-to-block table.</summary>
    private static readonly byte[] BlockToChannel =
        [9, 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15];

    private readonly EmulationFilter _filter = filter;

    private EmulationSettings Settings => _filter.Settings;
    private PanelState Panel => _filter.Panel;

    public void Handle(int port, ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != 0xF0) { Forward(port, data, reset: false); return; }

        // F0 41 10 42 12 <addr24> <data...>  Roland GS data set
        if (data.Length >= 9 && data[1] == 0x41 && data[2] == 0x10
            && data[3] == 0x42 && data[4] == 0x12)
        {
            HandleGs(port, data);
            return;
        }

        // F0 41 10 45 12 <addr24> <data...>  Roland display
        if (data.Length >= 11 && data[1] == 0x41 && data[2] == 0x10
            && data[3] == 0x45 && data[4] == 0x12)
        {
            int address = Address(data, 5);
            HandleRolandDisplay(address, RolandPayload(data, 8));
            Forward(port, data, reset: false);
            return;
        }

        // F0 43 10 4C 00 00 7E 00 F7  XG system reset
        if (data.Length >= 9 && data[1] == 0x43 && data[2] == 0x10 && data[3] == 0x4C
            && data[4] == 0 && data[5] == 0 && data[6] == 0x7E && data[7] == 0)
        {
            Panel.XgSystemReset = true;
            Panel.ResetReceive();
            Panel.Invalidate();
            Forward(port, data, reset: true);
            return;
        }

        // F0 43 10 4C <addr24> <data...>  XG parameter change
        if (data.Length >= 9 && data[1] == 0x43 && data[2] == 0x10 && data[3] == 0x4C)
        {
            int address = Address(data, 4);
            ReadOnlySpan<byte> payload = Payload(data, 7);
            if ((address & 0xFFFF00) is 0x060000 or 0x070000) HandleXgDisplay(address, payload);
            else HandleXgParameter(address, payload);

            // XG All Parameter Reset is not passed on.
            if (address == 0x00007F) return;

            Forward(port, data, reset: false);
            return;
        }

        // F0 7F dd 04 01 ll mm F7  Universal master volume. Only the MSB is kept: the
        // panel counts in the same 0-127 every other volume does.
        if (data.Length >= 8 && data[1] == 0x7F && data[3] == 0x04 && data[4] == 0x01)
        {
            Panel.MasterVolume = data[6] & 0x7F;
            Panel.Invalidate();
            Forward(port, data, reset: false);
            return;
        }

        // F0 7E 7F 09 01 F7  GM System On
        if (data.Length >= 6 && data[1] == 0x7E && data[2] == 0x7F
            && data[3] == 0x09 && data[4] == 0x01)
        {
            Panel.GmSystemOn = true;
            Panel.ResetReceive();
            Panel.Invalidate();
            Forward(port, data, reset: true);
            return;
        }

        Forward(port, data, reset: false);
    }

    /// <summary>
    /// Sends unless the settings say otherwise. Reset messages have their own switch so a
    /// module can be protected from being reinitialised without blocking all exclusives.
    /// </summary>
    private void Forward(int port, ReadOnlySpan<byte> data, bool reset)
    {
        if (reset && Settings.DisableResetExclusive) return;
        if (Settings.DisableExclusive && data.Length > 0 && data[0] == 0xF0) return;
        _filter.EmitLong(port, data);
    }

    private static int Address(ReadOnlySpan<byte> data, int offset)
        => data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];

    /// <summary>Bytes between the address and the closing F7.</summary>
    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> data, int start)
    {
        int end = start;
        while (end < data.Length && data[end] != 0xF7) end++;
        return start >= end ? [] : data[start..end];
    }

    /// <summary>
    /// Payload of a Roland message, which ends one byte before the F7 so the checksum is
    /// never taken as data.
    /// </summary>
    private static ReadOnlySpan<byte> RolandPayload(ReadOnlySpan<byte> data, int start)
    {
        int end = start;
        while (end + 1 < data.Length && data[end + 1] != 0xF7) end++;
        return start >= end ? [] : data[start..end];
    }

    private void HandleGs(int port, ReadOnlySpan<byte> data)
    {
        int address = Address(data, 5);
        ReadOnlySpan<byte> payload = RolandPayload(data, 8);
        bool isReset = address is 0x40007F or 0x00007F;

        if (isReset) Panel.ApplyGsReset();

        if (address == 0x40007F && payload.Length > 0)
        {
            Panel.GsReset = payload[0] != 0x7F;
        }
        else if (address == 0x00007F && payload.Length > 0)
        {
            Panel.SystemMode = payload[0] + 1;
        }
        else if (address == 0x000003)
        {
            // Not a parameter write. 88ProSetting holds it back the way DisableResetExclusive
            // holds back a reset, and DisableExclusive still applies on top of that.
            if (Settings.ProSetting) return;
            Forward(port, data, reset: false);
            return;
        }
        else if (address == 0x400300 && payload.Length >= 2)
        {
            Panel.Insertion1.Set(payload[1], payload[0]);
        }
        else if (!isReset)
        {
            for (int i = 0; i < payload.Length; i++)
                WriteGsParameter(port, address + i, payload[i]);
        }

        Panel.Invalidate();
        Forward(port, data, isReset);
    }

    /// <summary>
    /// Applies one GS parameter byte, and re-emits it as a controller when the connected
    /// module would not understand the exclusive.
    /// </summary>
    private void WriteGsParameter(int port, int address, byte value)
    {
        bool convertsToController = Settings.GsToGmEmu || Settings.GsToXgEmu || Settings.GsToX5dEmu;
        int blockAddress = address & 0xFFF0FF;
        int block = (address >> 8) & 0x0F;
        int channel = BlockToChannel[block];

        switch (blockAddress)
        {
            case 0x402010 when convertsToController:
                // BEND PITCH CONTROL, sent as RPN 0 (pitch bend sensitivity) plus a data
                // entry. The GS value is offset by 0x40, which is why it is subtracted here.
                // Below 0x40 it would be a range the RPN cannot say, and the module keeps the
                // one it has.
                if (value < 0x40) return;
                _filter.SendShort(port, (uint)(0xB0 | channel | 0x64 << 8));
                _filter.SendShort(port, (uint)(0xB0 | channel | 0x65 << 8));
                _filter.SendShort(port, (uint)(0xB0 | channel | 0x06 << 8 | (value - 0x40) << 16));
                return;

            case 0x401015:                                   // Use for Rhythm Part
                PartState part = _filter.Parts[port, channel];
                part.Mode = value switch
                {
                    0 => PartMode.Melodic,
                    1 => PartMode.Drum1,
                    _ => PartMode.Drum2,
                };
                _filter.Panel.Part(port, channel).Rhythm = value != 0;
                if (Settings.GsToX5dEmu)
                {
                    _filter.Emit(port, (uint)(0xB0 | channel | 0x3E << 16));
                    _filter.Emit(port, (uint)(0xC0 | channel));
                }
                return;

            case 0x401002:                                   // RX CHANNEL
            case 0x501002:
            {
                // The slot is the block mapped through to a channel, not the raw block, and
                // 50 is the B parts. Values of 0x10 and above mean the part receives nothing.
                int slot = channel | (address >> 16 == 0x50 ? 0x10 : 0);
                byte stored = value < 0x10
                    ? (byte)(Panel.PartPort[slot] << 4 | value)
                    : (byte)0x7F;
                StorePartReceive(slot, stored);
                EmitXgReceiveChannel(port, slot, stored);
                return;
            }

            // The B parts' own settings, which the panel shows for port B. Only the panel's
            // copy: the conversion works on the channels as they arrive, and a B part is not
            // one of them.
            case 0x501015 when port < 2:                     // Use for Rhythm Part
                Panel.Part(1, channel).Rhythm = value != 0;
                return;

            case 0x50101C when port < 2:                     // Panpot
                Panel.Part(1, channel).Panpot = value;
                return;

            case 0x40101C:                                   // Panpot
                _filter.Panel.Part(port, channel).Panpot = value;
                if (convertsToController)
                    _filter.SendShort(port, (uint)(0xB0 | channel | 0x0A << 8 | value << 16));
                return;
        }

        // GS Reset. HandleGs catches 40 00 7F whole, but a multi-byte write that runs into
        // this address, or any other block's 40 0n 7F, arrives here.
        if (blockAddress == 0x40007F) { Panel.GsReset = value != 0x7F; return; }

        switch (address)
        {
            case 0x400004: Panel.MasterVolume = value; return;
            case 0x400130: Panel.Reverb.Type = value; return;
            case 0x400138: Panel.Chorus.Type = value; return;
            case 0x400150: Panel.Variation.Type = value; return;
        }

        // CHANNEL MSG RX PORT: BLOCK00-BLOCK1F pick port A (0) or B (1) for each of the
        // 32 parts. Only that one bit is meaningful. As above, the low nibble of the
        // address is a block number and has to be mapped to a channel first.
        if ((address & 0xFFFFE0) == 0x000100)
        {
            int index = BlockToChannel[address & 0x0F] | (address & 0x10);
            Panel.PartPort[index] = (byte)(value & 1);
            byte was = Panel.PartReceive[index];
            byte stored = was == 0x7F ? was : (byte)((was & 0x0F) | ((value << 4) & 0x10));
            StorePartReceive(index, stored);
            EmitXgReceiveChannel(port, index, stored);
            return;
        }

        // 40 01 00 - 40 01 0F carry the patch name, one character each.
        if (address is > 0x4000FF and < 0x400110)
        {
            int index = address & 0x0F;
            Panel.PatchName[index] = (char)Math.Max(value, (byte)0x20);
        }
    }

    private void StorePartReceive(int index, byte value)
    {
        if ((uint)index >= Panel.PartReceive.Length) return;
        Panel.PartReceive[index] = value;
    }

    /// <summary>
    /// GS Rx Channel has an XG equivalent, so it can be carried across. The part number and
    /// the value both come from the slot that was just written, not from the raw message.
    /// </summary>
    private void EmitXgReceiveChannel(int port, int part, byte stored)
    {
        if (!Settings.GsToXgEmu) return;
        _filter.EmitLong(port,
            [0xF0, 0x43, 0x10, 0x4C, 0x08, (byte)part, 0x04, stored, 0xF7]);
    }

    private void HandleXgParameter(int address, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return;

        if (address == 0x000004) { Panel.MasterVolume = payload[0] & 0x7F; return; }

        if ((address & 0xFFFF00) == 0x020100)
        {
            int slot = (address >> 5) & 7;
            int offset = address & 0x1F;

            if (offset == 0 && payload.Length >= 2)
            {
                switch (slot)
                {
                    case 0: Panel.Reverb.Set(payload[1], payload[0]); break;
                    case 1: Panel.Chorus.Set(payload[1], payload[0]); break;
                    case 2: Panel.Variation.Set(payload[1], payload[0]); break;
                }
            }
            else if (slot == 2 && offset == 0x1A && payload[0] == 1)
            {
                Panel.VariationPart = 0xFF;          // system connection
            }
            else if (slot == 2 && offset == 0x1B && payload[0] != 0x7F)
            {
                Panel.VariationPart = payload[0];
            }
        }
        else if ((address & 0xFF0000) == 0x030000)
        {
            int slot = (address >> 8) & 0xFF;
            if ((address & 0x7F) == 0 && payload.Length >= 2)
            {
                if (slot == 0) Panel.Insertion1.Set(payload[1], payload[0]);
                else if (slot == 1) Panel.Insertion2.Set(payload[1], payload[0]);
            }
            else if ((address & 0x7F) == 0x0C)
            {
                if (slot == 0) Panel.Insertion1Part = payload[0];
                else if (slot == 1) Panel.Insertion2Part = payload[0];
            }
        }
        else if ((address & 0xFF00FF) is 0x080004 or 0x080072 or 0x080073
                                       or 0x080076 or 0x080077)
        {
            int part = (address >> 8) & 0x1F;
            int lowByte = address & 0xFF;
            if (lowByte == 0x04)
            {
                StorePartReceive(part, payload[0]);
                if (payload[0] < 0x20) Panel.PartPort[part] = (byte)(payload[0] >> 4);
                return;
            }

            DisplayPart display = Panel.Part(part / 16, part % 16);
            switch (lowByte)
            {
                case 0x72: display.EqBassGain = payload[0]; break;
                case 0x73: display.EqTrebleGain = payload[0]; break;
                case 0x76: display.EqBassFrequency = payload[0]; break;
                case 0x77: display.EqTrebleFrequency = payload[0]; break;
            }
        }

        Panel.Invalidate();
    }

    /// <summary>XG display: ASCII at <c>06 00 nn</c>, a 7-dots-per-byte bitmap at <c>07 nn xx</c>.</summary>
    private void HandleXgDisplay(int address, ReadOnlySpan<byte> payload)
    {
        if ((address & 0xFFFF00) == 0x060000)
        {
            int indent = address & 0xFF;
            Array.Fill(Panel.XgLcdText, ' ');
            for (int i = 0; i < payload.Length && indent + i < PanelState.XgLcdColumns; i++)
                Panel.XgLcdText[indent + i] = payload[i] == 0 ? ' ' : (char)payload[i];
            Panel.LcdHoldMs = 0xCE4;
            Panel.XgLcdVisible = true;
        }
        else
        {
            for (int i = 0; i < payload.Length && i < 0x30; i++)
            {
                // Both halves of the address move with the byte index, so a run that crosses
                // a 16-byte boundary steps the X offset on as well.
                int current = address + i;
                int x = ((current >> 4) & 0x0F) * 7;
                int at = (current & 0x0F) * 0x10 + x;

                // The leading two dots always land; the remaining five are dropped as a group
                // once the block would reach past column 15. That is how the last block of a
                // row still gets its two dots.
                int dots = x + 2 < 0x0F ? 7 : 2;
                for (int bit = 0; bit < dots; bit++)
                {
                    int index = at + bit;
                    if ((uint)index < PanelState.BitmapBytes)
                        Panel.Bitmap[index] = (byte)(payload[i] & (0x40 >> bit));
                }
            }
            Panel.BitmapRemainingMs = 0xCE4;
            Panel.BitmapVisible = true;
        }
        Panel.Invalidate();
    }

    /// <summary>Roland display: ASCII at <c>10 00 nn</c>, page control at <c>10 20 nn</c>.</summary>
    private void HandleRolandDisplay(int address, ReadOnlySpan<byte> payload)
    {
        if ((address & 0xFFFF00) == 0x100000)
        {
            WriteRolandText(payload);
        }
        else if (address == 0x102000 && payload.Length is 1 or 2)
        {
            if (payload.Length == 2) Panel.BitmapHoldMs = HoldFor(payload[1]);
            SelectBitmapPage(payload[0]);
        }
        else if (address == 0x102001 && payload.Length >= 1)
        {
            Panel.BitmapHoldMs = HoldFor(payload[0]);
        }
        else if ((address & 0xFFF000) == 0x100000 && (address & 0xFF) < 0x40)
        {
            // 10 nn xx with xx below 0x40: the even row group of the pair nn names.
            WriteRolandBitmap(((address >> 8) & 0x0F) * 2 - 2, address, payload, copyWhenLive: true);
        }
        else if ((address & 0xFFFF00) == 0x100100 && (address & 0xFF) is > 0x3F and <= 0x80)
        {
            // 10 01 40 - 10 01 80: row group 1. Only nn = 1 is taken this way, so the odd
            // groups above 1 have no address of their own.
            WriteRolandBitmap(1, address, payload, copyWhenLive: false);
        }

        Panel.Invalidate();
    }

    /// <summary>
    /// Lays out one Roland text message.
    /// </summary>
    /// <remarks>
    /// Up to 16 characters are centred on the line, with an odd space on the left as on the
    /// SC-88Pro, and held for three seconds. A longer message is instead framed by the patch
    /// name on both sides, separated by <c>'&lt;'</c>, and held for 300ms: the whole run is
    /// built in a scroll buffer and the leading 16 columns of it are shown.
    /// </remarks>
    private void WriteRolandText(ReadOnlySpan<byte> payload)
    {
        bool longLine = payload.Length > 0x10;
        var line = new System.Text.StringBuilder();

        if (!longLine) line.Append(' ', (PanelState.LcdColumns - payload.Length) / 2);
        else line.Append(Panel.PatchName).Append('<');

        for (int i = 0; i < payload.Length && i < 0x100; i++)
            line.Append(payload[i] < 0x20 ? ' ' : (char)payload[i]);

        if (!longLine)
        {
            while (line.Length < PanelState.LcdColumns) line.Append(' ');
        }
        else
        {
            line.Append('<').Append(Panel.PatchName);
        }

        Panel.LcdScroll = line.ToString();
        Array.Fill(Panel.LcdText, ' ');
        for (int i = 0; i < PanelState.LcdColumns && i < Panel.LcdScroll.Length; i++)
            Panel.LcdText[i] = Panel.LcdScroll[i];

        Panel.LcdHoldMs = longLine ? PanelState.LcdScrollStepMs : 3000;
        // A long line already shows its first 16 columns, so the next character to shift in
        // is the one past them. A short line does not scroll at all.
        Panel.LcdScrollIndex = longLine ? PanelState.LcdColumns : PanelState.NoScroll;
        Panel.XgLcdVisible = false;
    }

    /// <summary>
    /// Display Time as milliseconds.
    /// </summary>
    /// <remarks>
    /// In steps of <see cref="PanelState.BitmapHoldStepMs"/>. Anything past the top of the
    /// range is held there rather than wrapped, so it does not come out as no time.
    /// </remarks>
    private static int HoldFor(byte value)
        => Math.Min((int)value, PanelState.MaxBitmapHoldSteps) * PanelState.BitmapHoldStepMs;

    /// <summary>
    /// Puts up one of the stored pages, or takes the matrix off for the bar display.
    /// </summary>
    /// <remarks>
    /// Pages are numbered from one here and from zero in <see cref="PanelState.BitmapPages"/>.
    /// </remarks>
    private void SelectBitmapPage(int page)
    {
        if (page == 0)
        {
            Panel.BitmapRemainingMs = 0;
            Panel.BitmapVisible = false;
            return;
        }

        if ((uint)(page - 1) >= Panel.BitmapPages.Length) return;
        Panel.BitmapPages[page - 1].CopyTo(Panel.Bitmap, 0);
        ShowBitmap();
    }

    /// <summary>
    /// Puts the dot matrix on screen for the time the page control last set.
    /// </summary>
    /// <remarks>
    /// A display time of zero does not show it at all. Left visible it would stay for good:
    /// the countdown only runs while there is something left of it.
    /// </remarks>
    private void ShowBitmap()
    {
        Panel.BitmapRemainingMs = Panel.BitmapHoldMs;
        Panel.BitmapVisible = Panel.BitmapHoldMs > 0;
    }

    /// <summary>Five dots per byte, in bits 4 down to 0.</summary>
    private void WriteRolandBitmap(
        int rowGroup, int address, ReadOnlySpan<byte> payload, bool copyWhenLive)
    {
        if (rowGroup is >= 0 and < 10)
        {
            byte[] page = Panel.BitmapPages[rowGroup];
            for (int i = 0; i < payload.Length && i < 0x40; i++)
            {
                int current = address + i;
                int x = ((current >> 4) & 0x0F) * 5;
                int at = (current & 0x0F) * 0x10 + x;

                // The leading dot always lands; the other four go as a group once the block
                // would reach past column 15.
                int dots = x + 1 < 0x0F ? 5 : 1;
                for (int bit = 0; bit < dots; bit++)
                {
                    int index = at + bit;
                    if ((uint)index < PanelState.BitmapBytes)
                        page[index] = (byte)(payload[i] & (0x10 >> bit));
                }
            }
        }

        if (copyWhenLive && rowGroup == 0) Panel.BitmapPages[0].CopyTo(Panel.Bitmap, 0);
        ShowBitmap();
    }

    /// <summary>Builds a Roland data-set message, with the checksum the format requires.</summary>
    public void SendRolandParameter(int port, byte model, int address, ReadOnlySpan<byte> data)
    {
        Span<byte> message = stackalloc byte[data.Length + 10];
        message[0] = 0xF0;
        message[1] = 0x41;
        message[2] = 0x10;
        message[3] = model;
        message[4] = 0x12;
        message[5] = (byte)(address >> 16);
        message[6] = (byte)(address >> 8);
        message[7] = (byte)address;
        data.CopyTo(message[8..]);

        int sum = 0;
        for (int i = 5; i < 8 + data.Length; i++) sum += message[i];
        message[8 + data.Length] = (byte)((0x80 - (sum & 0x7F)) & 0x7F);
        message[^1] = 0xF7;

        _filter.EmitLong(port, message);
    }
}
