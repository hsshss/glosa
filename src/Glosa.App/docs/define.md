# define.yaml — Glosa's target modules

[日本語](define.ja.md)

The list of target modules, and the rules for telling which one a song was written for. It
sits in the same folder as the program: beside the executable on Windows and Linux, and
inside `Glosa.app/Contents/MacOS` on macOS.

## Changing the definitions

Do not edit `define.yaml` directly; edit the override file instead.

In the settings folder (Settings > Open Settings Folder):

1. Rename `define.override.yaml.sample` to `define.override.yaml`.
2. Edit `define.override.yaml`, and restart Glosa.

- When `define.override.yaml` is there, it is read in place of `define.yaml`.
- The sample is written afresh from the latest `define.yaml` at every start. To take in a
  new release's changes, compare the override with it.
- If the override cannot be read, the shipped `define.yaml` is used, and a message at startup
  says why.

With Preferences > "Detect target modules with the TMIDI definition file" turned on,
`patterns` and `dataDetection` go unused.

## version

The version of the format. Leave it as the sample has it.

## modules

The list of target modules. This list, in this order, is what the playlist's Target Module
menu and the like offer. `THRU`, `GS`, `XG` and `CM-64` are used by Glosa itself, so they
are added back if they are left out.

| Key | Meaning |
|---|---|
| `name` | The module's name. Use the same name as the TMIDI definition file's `[tdfindex]`. |
| `patterns` | Regular expressions that detect this module (see below). |
| `fallback` | When `true`, checked only when no other module matched. |
| `initializeType` | The kind of initialization (see below). |

### patterns

- .NET regular expressions, each in YAML single quotes (a `'` itself is written `''`). Case
  is ignored.
- Full-width letters, digits and symbols are made half-width before matching, so half-width
  is all a pattern needs.
- The text is not cut into words. What must not come before or after a match is written in
  the pattern. The patterns shipped use `(?<![0-9A-Za-z])` before and `(?![0-9A-UW-Za-uw-z])`
  after (letting the V of `Ver` and the like follow).
- `^` and `$` match at the start and end of each source searched: the file name, the song
  title and so on. In one with several lines, such as the attached text, they match at the
  start and end of each line too.
- Where two patterns match the same characters, the one listed first wins. List the longer
  model numbers first (`8850` before `88`).

### initializeType

While no TMIDI definition file is loaded and a port map's Module is this module, sent before
each song to the map's ports marked Reset. Not sent with Preferences > "Reset the output
module before each song" turned off.

| Value | Sends |
|---|---|
| `GM` | GM System On |
| `GS` | GS Reset |
| `SC88` | GS Reset, and the system mode setting of the SC-88 and later |
| `XG` | XG System On |
| `MT32` | Reset All Controllers on every channel |

## nameDetection

| Key | Meaning |
|---|---|
| `maxDocumentLength` | How many characters from the start of the attached text are searched. |

## dataDetection

When the name did not settle it, the initialization messages of the GS, XG and CM-64 near the
start of the song decide. Unused with "Song data" unticked under "Information to detect
from" in Preferences.

| Key | Meaning |
|---|---|
| `maxMessages` | How many messages from the start of the song are looked at. |

## What is searched

Which sources are searched, in what order, and which model wins when more than one is named,
are set in Preferences > Target Module Detection.
