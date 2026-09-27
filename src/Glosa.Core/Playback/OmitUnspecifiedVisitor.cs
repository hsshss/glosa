using System.Collections;
using System.Collections.Concurrent;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.ObjectGraphVisitors;

namespace Glosa.Core.Playback;

/// <summary>
/// Leaves out of a file every value nobody has set: whatever a freshly made object of the
/// same kind would hold, nulls, and empty lists.
/// </summary>
/// <remarks>
/// A missing key reads back as that same fresh value, so nothing is lost by not writing it,
/// and the file shrinks to what is actually particular to it — a playlist entry to its path
/// and length, the settings to what was changed.
///
/// The comparison is against a fresh object, not against the type's own default. Much here
/// starts somewhere other than zero or false (the port map switch is on, the last-played row
/// is -1), and leaving out a false that reads back as true would change the setting.
/// </remarks>
internal sealed class OmitUnspecifiedVisitor(IObjectGraphVisitor<IEmitter> next)
    : ChainedObjectGraphVisitor(next)
{
    /// <summary>A fresh object of each kind met, made once.</summary>
    private static readonly ConcurrentDictionary<Type, object?> Fresh = new();

    /// <summary>The fresh counterpart of each object being written, innermost on top.</summary>
    private readonly Stack<object?> _counterparts = new();

    public override void VisitMappingStart(IObjectDescriptor mapping, Type keyType, Type valueType,
                                           IEmitter context, ObjectSerializer serializer)
    {
        _counterparts.Push(FreshOf(mapping.Value?.GetType() ?? mapping.Type));
        base.VisitMappingStart(mapping, keyType, valueType, context, serializer);
    }

    public override void VisitMappingEnd(IObjectDescriptor mapping, IEmitter context,
                                         ObjectSerializer serializer)
    {
        _counterparts.TryPop(out _);
        base.VisitMappingEnd(mapping, context, serializer);
    }

    public override bool EnterMapping(IPropertyDescriptor key, IObjectDescriptor value,
                                      IEmitter context, ObjectSerializer serializer)
    {
        object? fresh = _counterparts.TryPeek(out object? counterpart) && counterpart is not null
            ? key.Read(counterpart).Value
            : null;

        return !Unspecified(value.Value, fresh)
            && base.EnterMapping(key, value, context, serializer);
    }

    private static bool Unspecified(object? value, object? fresh)
    {
        if (value is null) return true;

        // A list is left out when it holds what the fresh one holds, item for item; an empty
        // one where the fresh one has nothing to compare with. Leaving out an emptied list
        // that starts full would read back full.
        if (value is IEnumerable items and not string)
            return fresh is IEnumerable start
                ? items.Cast<object?>().SequenceEqual(start.Cast<object?>())
                : !items.Cast<object?>().Any();

        return value.Equals(fresh);
    }

    private static object? FreshOf(Type type) => Fresh.GetOrAdd(type, static t =>
    {
        try
        {
            return t.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(t);
        }
        catch (Exception ex) when (ex is MemberAccessException
                                      or System.Reflection.TargetInvocationException)
        {
            // Nothing to compare with, so everything is written.
            return null;
        }
    });
}
