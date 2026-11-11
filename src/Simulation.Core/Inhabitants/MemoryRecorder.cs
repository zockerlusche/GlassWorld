using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// Turns reported core events into remembered events of the inhabitants they are about (B4a R5). Which
/// events are remembered, under which kind, and which are formative (always, or the first time for this
/// kind and subject) comes from the knowledge data (memory.events); the code names no event, action or
/// effect. What an event is about follows from its target: another inhabitant, the object type of a thing
/// (a type is what knowledge is about; the thing itself may be gone), or nothing. Not a world system: the
/// memory is part of each inhabitant's state.
/// </summary>
public sealed class MemoryRecorder
{
    private readonly KnowledgeMemory? _settings;
    private readonly FrozenDictionary<string, KnowledgeMemoryEvent[]> _byEvent;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly ActionRules _actions;

    /// <summary>Creates the recorder from the data (null = no memory: nothing is remembered).</summary>
    public MemoryRecorder(KnowledgeMemory? settings, InhabitantsSystem inhabitants, WorldObjects objects, ActionRules actions)
    {
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(actions);
        _settings = settings;
        _byEvent = (settings?.Events ?? [])
            .GroupBy(e => e.Event, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        _inhabitants = inhabitants;
        _objects = objects;
        _actions = actions;
    }

    /// <summary>Takes a reported core event; every data entry that fits gives one remembered event.</summary>
    public void Observe(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        if (_settings is null
            || !_byEvent.TryGetValue(worldEvent.Kind, out var entries)
            || _inhabitants.ResolveTarget(worldEvent.InhabitantId)?.Inhabitant is not { } actor)
        {
            return;
        }

        var other = worldEvent.TargetId is { } targetId ? _inhabitants.ResolveTarget(targetId)?.Inhabitant : null;
        var about = About(worldEvent, other);
        var notes = new List<Note>();
        foreach (var entry in entries)
        {
            if (!Fits(entry, worldEvent) || (entry.OnlyWithSubject == true && about is null))
            {
                continue;
            }

            var kind = entry.ActionAsKind == true ? worldEvent.Action ?? worldEvent.Kind : entry.Kind ?? worldEvent.Kind;
            Add(notes, new Note(actor, kind, about?.Kind, about?.Id, entry.Formative));
            if (entry.Both == true && other is not null)
            {
                Add(notes, new Note(other, kind, InhabitantsSystem.AboutInhabitant, actor.Id, entry.Formative));
            }
        }

        foreach (var note in notes)
        {
            var formative = note.Formative switch
            {
                KnowledgeMemoryEventFormative.Always => true,
                KnowledgeMemoryEventFormative.First => note.Who.Memory.First(note.Kind, note.AboutKind, note.AboutId),
                _ => false,
            };
            note.Who.Memory.Remember(worldEvent.Tick, note.Kind, note.AboutKind, note.AboutId, formative, _settings.MaxEvents);
        }
    }

    /// <summary>One event gives one memory per inhabitant, kind and subject; of two entries the more formative counts.</summary>
    private static void Add(List<Note> notes, Note note)
    {
        var index = notes.FindIndex(n => ReferenceEquals(n.Who, note.Who) && n.Kind == note.Kind && n.AboutKind == note.AboutKind && n.AboutId == note.AboutId);
        if (index < 0)
        {
            notes.Add(note);
        }
        else if (note.Formative > notes[index].Formative)
        {
            notes[index] = note;
        }
    }

    private (string Kind, string Id)? About(WorldEvent worldEvent, Inhabitant? other)
    {
        if (worldEvent.TargetId is not { } target)
        {
            return null;
        }

        if (other is not null)
        {
            return (InhabitantsSystem.AboutInhabitant, other.Id);
        }

        if (worldEvent.TargetType is { } type)
        {
            return (InhabitantsSystem.AboutType, type);
        }

        if (_objects.TryGet(target, out var thing))
        {
            return (InhabitantsSystem.AboutType, thing.TypeId);
        }

        // Some events name the type itself (knowledge gained, an invention).
        return _objects.Catalog.Contains(target) ? (InhabitantsSystem.AboutType, target) : null;
    }

    private bool Fits(KnowledgeMemoryEvent entry, WorldEvent worldEvent)
    {
        if (entry.DetailEndsWith is { } ending && worldEvent.Detail?.EndsWith(ending, StringComparison.Ordinal) != true)
        {
            return false;
        }

        if (worldEvent.Action is { } action && entry.LeftOutActions?.Contains(action) == true)
        {
            return false;
        }

        return entry.Effect is not { } wanted
            || (worldEvent.Action is { } done && _actions.Applying(done, worldEvent.TargetType).SelectMany(r => r.Effects).Any(e => Matches(wanted, e)));
    }

    private static bool Matches(KnowledgeMemoryEffect wanted, Effect effect)
    {
        var sameKind = wanted.Kind switch
        {
            KnowledgeMemoryEffectKind.Need => effect.Kind == EffectKind.Need,
            KnowledgeMemoryEffectKind.Mood => effect.Kind == EffectKind.Mood,
            _ => effect.Kind == EffectKind.Will_to_live,
        };
        return sameKind
            && (wanted.Id is null || wanted.Kind == KnowledgeMemoryEffectKind.Will_to_live || string.Equals(wanted.Id, effect.Id, StringComparison.Ordinal))
            && (wanted.DeltaAtMost is not { } most || effect.Delta <= most)
            && (wanted.DeltaAtLeast is not { } least || effect.Delta >= least);
    }

    private readonly record struct Note(Inhabitant Who, string Kind, string? AboutKind, string? AboutId, KnowledgeMemoryEventFormative Formative);
}
