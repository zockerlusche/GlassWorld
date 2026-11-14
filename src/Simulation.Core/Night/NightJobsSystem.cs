using System.Collections.Frozen;
using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Safety;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Night;

/// <summary>An event that went out with a memory job.</summary>
/// <param name="Seq">Its running number in the inhabitant's memory.</param>
/// <param name="Tick">Its tick (the answer names kept events by tick).</param>
/// <param name="Formative">Whether it was sent as formative.</param>
public sealed record SentEvent(long Seq, long Tick, bool Formative);

/// <summary>An open memory job (B4a R5): the events it carries and the text budget it asked for.</summary>
/// <param name="RequestId">The job.</param>
/// <param name="InhabitantId">Whose memory.</param>
/// <param name="Tick">Tick it was sent.</param>
/// <param name="MaxChars">The text budget sent with it (the answer is checked against this, not against later data).</param>
/// <param name="Sent">The events sent, oldest first.</param>
/// <param name="Review">A1585: sent as a nightly look back (job kind review) instead of a memory job.</param>
public sealed record MemoryJob(string RequestId, string InhabitantId, long Tick, int MaxChars, IReadOnlyList<SentEvent> Sent, bool Review = false);

/// <summary>An open question job: a guess for an open question about an object type.</summary>
/// <param name="RequestId">The job.</param>
/// <param name="InhabitantId">Who asks.</param>
/// <param name="Tick">Tick it was sent.</param>
/// <param name="QuestionId">The open question (decisions system).</param>
/// <param name="TypeId">The object type it is about.</param>
public sealed record QuestionJob(string RequestId, string InhabitantId, long Tick, string QuestionId, string TypeId);

/// <summary>Complete state of the night jobs (save games).</summary>
/// <param name="Counter">Request counter.</param>
/// <param name="MemoryJobs">Open memory jobs in request id order.</param>
/// <param name="QuestionJobs">Open question jobs in request id order.</param>
/// <param name="QuestionsWaiting">Inhabitants whose questions of this night wait for their memory job.</param>
public sealed record NightJobsState(long Counter, IReadOnlyList<MemoryJob> MemoryJobs, IReadOnlyList<QuestionJob> QuestionJobs, IReadOnlyList<string> QuestionsWaiting);

/// <summary>
/// The inhabitants' work of the compute night (B7, W1764; a world system): when the night begins, every
/// inhabitant first has its memory condensed (job <c>memory</c>, once enough events are not in the summary
/// yet), then gets a guess for each of its open questions about an object type (job <c>question</c>) - in
/// this order, because the question reads the summary. The AI only proposes (B2462): the core checks every
/// answer itself and drops one that does not fit without changing anything. A guess becomes a belief with
/// source <c>guessed</c>, at most as sure as the data's <c>guessed_cap</c>, and never replaces a belief the
/// inhabitant already has. Requests and outcomes go through the world's job log, so a replay gives the same
/// state. Without a job port the system is dormant and stays out of the world hash; every value is data.
/// </summary>
public sealed class NightJobsSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>Note of an answer the core refused; the reasons follow after the colon.</summary>
    public const string Rejected = "rejected:";

    /// <summary>Refused memory answer: the summary is empty.</summary>
    public const string SummaryEmpty = "summary_empty";

    /// <summary>Refused memory answer: the summary is longer than the text budget of the job.</summary>
    public const string SummaryTooLong = "summary_too_long";

    /// <summary>Refused memory answer: it keeps an event that was not sent.</summary>
    public const string KeptNotSent = "kept_not_sent";

    /// <summary>Refused memory answer: it drops a formative event.</summary>
    public const string FormativeDropped = "formative_dropped";

    /// <summary>Refused question answer: it guesses a property the vocabulary does not know.</summary>
    public const string UnknownProperty = "unknown_property";

    /// <summary>Refused question answer: it guesses the same property twice.</summary>
    public const string PropertyTwice = "property_twice";

    /// <summary>Note of a question answer that did not understand: the question stays open.</summary>
    public const string NotUnderstood = "not_understood";

    /// <summary>Note of an outcome that is not the final state of the job the core sent.</summary>
    public const string NotFinal = "not_final";

    /// <summary>Detail of <c>question_answered</c> when the compute night settled the question with a guess.</summary>
    public const string Guessed = "guessed";

    /// <summary>
    /// Event: the night went through an inhabitant's open questions about things. Detail =
    /// <c>asked=N,skipped=M</c>; skipped = questions that wait because there is nothing to guess from.
    /// </summary>
    public const string NightQuestions = "night_questions";

    /// <summary>Most noticed properties in a question job (contract generation-job: perception maxItems).</summary>
    public const int MaxPerception = 16;

    private const string TypeSubject = "type:";

    /// <summary>Root kind of an idea that grows from another idea.</summary>
    public const string IdeaKind = "idea";

    /// <summary>Event: a look back brought a new idea or let one grow (target = idea id; detail new / grew).</summary>
    public const string IdeaEvent = "idea";

    /// <summary>Event of a shown thought (the same kind the decider's and the question's thoughts report).</summary>
    public const string ThoughtEvent = "thought";

    /// <summary>Event: the inhabitant dreamed (text = the dream, detail = its tone, target = what it was about).</summary>
    public const string DreamEvent = "dreamed";

    private readonly SortedDictionary<string, MemoryJob> _memoryJobs = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, QuestionJob> _questionJobs = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _questionsWaiting = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _resume = new(StringComparer.Ordinal);
    private readonly KnowledgeSettings? _knowledge;
    private readonly FrozenSet<string> _nightPhases;
    private readonly FrozenSet<string> _properties;
    private readonly TextGuard _guard;
    private readonly InhabitantsSystem _inhabitants;
    private readonly DecisionsSystem _decisions;
    private readonly IJobPort? _port;
    private string _displayLocale;
    private long _counter;

    /// <summary>Creates the system. <paramref name="jobs"/> null = dormant (no AI connected).</summary>
    /// <param name="knowledge">Knowledge data (memory, night_questions, guessed_cap); null = nothing to do.</param>
    /// <param name="nightPhases">Day phases that are the compute night (content/jobs/phases.json).</param>
    /// <param name="properties">The property vocabulary: a guess may only name these.</param>
    /// <param name="guard">The content guard for the thought of an answer.</param>
    /// <param name="inhabitants">The inhabitants.</param>
    /// <param name="decisions">The open questions.</param>
    /// <param name="jobs">The world's job port, shared with the other job systems.</param>
    /// <param name="log">The world's job log (requests and the outcomes that took effect).</param>
    /// <param name="displayLocale">Language of the texts the jobs write.</param>
    public NightJobsSystem(KnowledgeSettings? knowledge, FrozenSet<string> nightPhases, FrozenSet<string> properties, TextGuard guard, InhabitantsSystem inhabitants, DecisionsSystem decisions, JobRouter? jobs, DecisionLog log, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(nightPhases);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(log);
        _knowledge = knowledge;
        _nightPhases = nightPhases;
        _properties = properties;
        _guard = guard;
        _inhabitants = inhabitants;
        _decisions = decisions;
        _port = jobs?.Claim(id => _memoryJobs.ContainsKey(id) || _questionJobs.ContainsKey(id));
        Log = log;
        _displayLocale = displayLocale;
    }

    /// <inheritdoc/>
    public string Name => "night_jobs";

    /// <summary>
    /// Whether a request is validated against its schema before it leaves (ADR-0009; set by the host from
    /// <see cref="SimulationHostOptions.ValidateRequests"/>): on in tests and check runs, off in the running game.
    /// </summary>
    public bool ValidateRequests { get; init; } = true;

    /// <summary>Language of the texts the jobs write (display_locale; set with the player's language).</summary>
    public string DisplayLocale
    {
        get => _displayLocale;
        internal set => _displayLocale = value;
    }

    /// <inheritdoc/>
    public bool HasState => _counter > 0 || _memoryJobs.Count > 0 || _questionJobs.Count > 0 || _questionsWaiting.Count > 0;

    /// <summary>The world's job log (shared with the invention system).</summary>
    public DecisionLog Log { get; }

    /// <summary>Open memory jobs in request id order.</summary>
    public IEnumerable<MemoryJob> MemoryJobs => _memoryJobs.Values;

    /// <summary>Open question jobs in request id order.</summary>
    public IEnumerable<QuestionJob> QuestionJobs => _questionJobs.Values;

    /// <summary>Whether the compute night begins with this tick: its day phase is a night phase and the one of the tick before was not.</summary>
    public bool NightBegins(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Tick > 0 && IsNight(world, world.Tick) && !IsNight(world, world.Tick - 1);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_port is null)
        {
            return;
        }

        foreach (var message in _port.Receive(world.Tick))
        {
            Apply(world, message);
        }

        var begins = NightBegins(world);
        if (!begins && _resume.Count == 0)
        {
            return;
        }

        // In id order (the inhabitants are kept sorted).
        foreach (var inhabitant in _inhabitants.All.ToList())
        {
            if (begins || _resume.Contains(inhabitant.Id))
            {
                Begin(world, inhabitant);
            }
        }

        _resume.Clear();
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_memoryJobs.Count);
        foreach (var job in _memoryJobs.Values)
        {
            hasher.Write(job.RequestId).Write(job.InhabitantId).Write(job.Tick).Write(job.MaxChars).Write(job.Sent.Count);
            if (job.Review)
            {
                // Only for review jobs, so states with memory jobs keep their hashes.
                hasher.Write("review");
            }

            foreach (var sent in job.Sent)
            {
                hasher.Write(sent.Seq).Write(sent.Tick).Write(sent.Formative);
            }
        }

        hasher.Write(_questionJobs.Count);
        foreach (var job in _questionJobs.Values)
        {
            hasher.Write(job.RequestId).Write(job.InhabitantId).Write(job.Tick).Write(job.QuestionId).Write(job.TypeId);
        }

        hasher.Write(_questionsWaiting.Count);
        foreach (var id in _questionsWaiting)
        {
            hasher.Write(id);
        }
    }

    /// <summary>Takes the complete state.</summary>
    public NightJobsState Capture() => new(_counter, [.. _memoryJobs.Values], [.. _questionJobs.Values], [.. _questionsWaiting]);

    /// <summary>Restores a captured state (null = a save game from before: nothing is open).</summary>
    public void Restore(NightJobsState? state)
    {
        if (state is null)
        {
            return;
        }

        _counter = state.Counter;
        foreach (var job in state.MemoryJobs)
        {
            _memoryJobs[job.RequestId] = job;
        }

        foreach (var job in state.QuestionJobs)
        {
            _questionJobs[job.RequestId] = job;
        }

        _questionsWaiting.UnionWith(state.QuestionsWaiting);
    }

    /// <summary>
    /// After loading with a live AI service: jobs that went to the previous process will never be answered, so
    /// they are given up and their inhabitants begin anew at the next tick, memory first (B2487). Not used
    /// for hash comparisons or replays.
    /// </summary>
    public void ResendPendingAfterLoad()
    {
        _resume.UnionWith(_memoryJobs.Values.Select(j => j.InhabitantId));
        _resume.UnionWith(_questionJobs.Values.Select(j => j.InhabitantId));
        _resume.UnionWith(_questionsWaiting);
        _memoryJobs.Clear();
        _questionJobs.Clear();
        _questionsWaiting.Clear();
    }

    private bool IsNight(World world, long tick) =>
        _nightPhases.Contains(world.Calendar.At(tick).Phase.ToString().ToLowerInvariant());

    /// <summary>One inhabitant's night: the memory first; its questions follow once no memory job of it is open.</summary>
    private void Begin(World world, Inhabitant inhabitant)
    {
        if (!HasMemoryJob(inhabitant) && Due(inhabitant))
        {
            SubmitMemory(world, inhabitant);
        }

        if (HasMemoryJob(inhabitant))
        {
            _questionsWaiting.Add(inhabitant.Id);
            return;
        }

        SubmitQuestions(world, inhabitant);
    }

    /// <summary>After a memory job: the next batch if the memory is still due and the last one was taken, else the questions that waited.</summary>
    private void Proceed(World world, Inhabitant inhabitant, bool condensed)
    {
        if (condensed && Due(inhabitant))
        {
            SubmitMemory(world, inhabitant);
            return;
        }

        if (_questionsWaiting.Remove(inhabitant.Id))
        {
            SubmitQuestions(world, inhabitant);
        }
    }

    private bool HasMemoryJob(Inhabitant inhabitant) => _memoryJobs.Values.Any(j => j.InhabitantId == inhabitant.Id);

    private bool Due(Inhabitant inhabitant) =>
        _knowledge?.Memory is { } memory && inhabitant.Memory.Uncondensed.Count() >= memory.CondenseAt;

    /// <summary>
    /// The next batch for a memory job: the oldest events the summary does not hold yet, at most
    /// job_max_events, ending before a formative event beyond job_max_formative.
    /// </summary>
    private static List<MemoryEntry> Batch(InhabitantMemory memory, KnowledgeMemory settings)
    {
        var batch = new List<MemoryEntry>();
        var formative = 0;
        foreach (var entry in memory.Uncondensed)
        {
            if (batch.Count >= settings.JobMaxEvents || (entry.Formative && formative >= settings.JobMaxFormative))
            {
                break;
            }

            formative += entry.Formative ? 1 : 0;
            batch.Add(entry);
        }

        return batch;
    }

    private void SubmitMemory(World world, Inhabitant inhabitant)
    {
        var settings = _knowledge!.Memory!;
        var batch = Batch(inhabitant.Memory, settings);
        if (batch.Count == 0)
        {
            return;
        }

        if (_knowledge.Review is { Enabled: true } review)
        {
            SubmitReview(world, inhabitant, settings, review, batch);
            return;
        }

        var job = NewJob(world, inhabitant, JobKind.Memory, "/memory", settings.JobPriority);
        job.Payload = new JobPayload
        {
            Memory = new MemoryPayload
            {
                Summary = inhabitant.Memory.Summary,
                Events = [.. batch.Select(e => new MemoryFact
                {
                    Tick = e.Tick,
                    Kind = e.Kind,
                    About = InhabitantStates.About(e),
                    Formative = e.Formative,
                    Times = InhabitantStates.TimesOf(e),
                    Outcome = InhabitantStates.OutcomeOf(e),

                    // What the inhabitant calls the subject now: a name learned after the event is there.
                    Label = _inhabitants.LabelOf(inhabitant, e),
                })],
                MaxChars = settings.MaxChars,
            },
        };
        Send(world, job);

        // What is out with a job stays what it was: a repetition from now on starts a new entry.
        inhabitant.Memory.Seal(batch[^1].Seq);
        _memoryJobs[job.RequestId] = new MemoryJob(job.RequestId, inhabitant.Id, world.Tick, settings.MaxChars, [.. batch.Select(e => new SentEvent(e.Seq, e.Tick, e.Formative))]);
    }

    /// <summary>
    /// A1585 (B7 R18, B2592, B2687): the nightly look back instead of the memory job - the same batch of events, plus
    /// what the inhabitant believes (its Fundbuch: experienced and heard first, newest first, at most knowledge_max),
    /// its ideas, needs, character and the actions an intention may name (all data).
    /// </summary>
    private void SubmitReview(World world, Inhabitant inhabitant, KnowledgeMemory memory, KnowledgeReview review, List<MemoryEntry> batch)
    {
        var fundbuch = inhabitant.Beliefs.All
            .OrderBy(b => b.Belief.Source == KnowledgeSources.Seen ? 1 : 0)
            .ThenByDescending(b => b.Belief.Tick)
            .ThenBy(b => b.TypeId, StringComparer.Ordinal)
            .ThenBy(b => b.PropertyId, StringComparer.Ordinal)
            .Take(review.KnowledgeMax)
            .Select(b => InhabitantStates.Entry(inhabitant, b.TypeId, b.PropertyId, b.Belief, _inhabitants.TypeLabelOf))
            .ToList();
        var job = NewJob(world, inhabitant, JobKind.Review, "/review", review.JobPriority);
        job.Payload = new JobPayload
        {
            Review = new ReviewPayload
            {
                Events = [.. batch.Select(e => new MemoryFact
                {
                    Tick = e.Tick,
                    Kind = e.Kind,
                    About = InhabitantStates.About(e),
                    Formative = e.Formative,
                    Times = InhabitantStates.TimesOf(e),
                    Outcome = InhabitantStates.OutcomeOf(e),
                    Label = _inhabitants.LabelOf(inhabitant, e),
                })],
                Memory = inhabitant.Memory.Summary,
                MemoryMaxChars = memory.MaxChars,
                DreamMaxChars = review.DreamMaxChars,
                Mood = inhabitant.Moods.Where(m => m.Value > 0).OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key).FirstOrDefault(),
                Tones = [.. review.Tones],
                Knowledge = fundbuch,
                Ideas = [.. inhabitant.Ideas.Ideas.Select(i => new ReviewIdea
                {
                    Id = i.Id,
                    Text = i.Text,
                    Roots = [.. i.Roots.Where(r => r.Kind != IdeaKind).Select(r => new Subject { Kind = Enum.Parse<SubjectKind>(r.Kind, ignoreCase: true), Id = r.Id })],
                    Strength = i.Strength,
                })],
                Needs = [.. inhabitant.Needs.Select(n => new LevelEntry { Id = n.Key, Value = n.Value })],
                Traits = [.. inhabitant.Traits.Select(t => new LevelEntry { Id = t.Key, Value = t.Value })],
                Actions = [.. review.IntentionActions],
                MaxNewIdeas = review.MaxNewIdeas,
                MaxIntentions = review.MaxIntentions,
            },
        };
        Send(world, job);
        inhabitant.Memory.Seal(batch[^1].Seq);
        _memoryJobs[job.RequestId] = new MemoryJob(job.RequestId, inhabitant.Id, world.Tick, memory.MaxChars, [.. batch.Select(e => new SentEvent(e.Seq, e.Tick, e.Formative))], Review: true);
    }

    /// <summary>
    /// A1585: what a look back leaves besides the memory - checked again by the core (GW1 #22713): an idea's roots must
    /// be beliefs, ideas or made things of this inhabitant now, an intention's action one of the data's and its subject
    /// a type it believes something about; anything else is dropped and logged. Thoughts and the dream pass the content
    /// guard and become thought / dream events (the morning sees them); the morning mood rises by nothing the AI
    /// sets - only the mood named is noted as an event (effects as data come later).
    /// </summary>
    private void Reflect(World world, MemoryJob job, ReviewResult result, string json)
    {
        if (_inhabitants.ResolveTarget(job.InhabitantId)?.Inhabitant is not { } inhabitant || _knowledge?.Review is not { } review)
        {
            return;
        }

        var dropped = new List<string>();
        var known = inhabitant.Beliefs.All.Select(b => b.TypeId).ToHashSet(StringComparer.Ordinal);
        var ideaIds = inhabitant.Ideas.Ideas.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var newIdeas = 0;
        foreach (var idea in result.Ideas)
        {
            var roots = idea.Roots.Select(r => new IdeaRoot(r.Kind.ToString().ToLowerInvariant(), r.Id)).ToList();
            var grows = idea.Id is { } id && ideaIds.Contains(id) ? id : null;
            // Each root by its kind (review GW1 #328): a type it believes something about, one of its ideas, someone whose name it knows.
            if (string.IsNullOrWhiteSpace(idea.Text) || roots.Count == 0 || roots.Any(r => !RootKnown(r, known, ideaIds, inhabitant))
                || (grows is null && ++newIdeas > review.MaxNewIdeas))
            {
                dropped.Add("idea");
                continue;
            }

            var kept = inhabitant.Ideas.Take(grows, idea.Text.Trim(), roots, world.Tick, review.IdeaStart, review.IdeaGrow, review.MaxIdeas);
            _inhabitants.Report(new WorldEvent(IdeaEvent, world.Tick, inhabitant.Id, null, kept.Id) { Detail = grows is null ? "new" : "grew" });
            if (idea.Thought is { } thought && _guard.Check(thought.Text).Allowed)
            {
                _inhabitants.Report(new WorldEvent(ThoughtEvent, world.Tick, inhabitant.Id, null, null) { Text = thought.Text });
            }
        }

        var actions = review.IntentionActions.ToHashSet(StringComparer.Ordinal);
        var intentions = new List<Intention>();
        foreach (var intention in result.Intentions.Take(review.MaxIntentions))
        {
            var aboutKind = intention.About?.Kind.ToString().ToLowerInvariant();
            if (!actions.Contains(intention.Action) || (intention.About is { } about && (about.Kind != SubjectKind.Type || !known.Contains(about.Id))))
            {
                dropped.Add("intention");
                continue;
            }

            var shown = intention.Thought is { } said && _guard.Check(said.Text).Allowed ? said.Text : null;
            intentions.Add(new Intention(intention.Action, aboutKind, intention.About?.Id, world.Tick, shown));
            if (intention.Thought is { } thought && _guard.Check(thought.Text).Allowed)
            {
                _inhabitants.Report(new WorldEvent(ThoughtEvent, world.Tick, inhabitant.Id, null, null) { Text = thought.Text });
            }
        }

        inhabitant.Ideas.Intend(intentions);
        if (result.Dream is { } dream && _guard.Check(dream.Text.Text).Allowed)
        {
            _inhabitants.Report(new WorldEvent(DreamEvent, world.Tick, inhabitant.Id, null, dream.About?.Id) { Text = dream.Text.Text, Detail = dream.Tone });
        }

        if (dropped.Count > 0)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, job.RequestId, world.Tick, json, Rejected + string.Join(',', dropped)));
        }
    }

    private static bool RootKnown(IdeaRoot root, HashSet<string> types, HashSet<string> ideas, Inhabitant inhabitant) => root.Kind switch
    {
        "type" => types.Contains(root.Id),
        IdeaKind => ideas.Contains(root.Id),
        "inhabitant" => inhabitant.KnownNames.Contains(root.Id),
        _ => false,
    };

    /// <summary>
    /// A question job for every open question of the inhabitant about an object type that the data list and
    /// that has none yet - unless the inhabitant has nothing to guess from: it has neither noticed anything
    /// about the thing nor believes anything about its kind. Such a question waits (it stays open) until the
    /// inhabitant has looked at the thing or tried it; a guess from nothing would only echo other memories.
    /// How many were asked and how many wait is reported as one event per inhabitant.
    /// </summary>
    /// <summary>The world's type truth (runtime types included); only for what a thing shows at a glance (D971).</summary>
    public ITypeTruth? Truth { get; set; }

    /// <summary>What a thing shows at a glance (D971): the looking data's visible properties it truly has, in data order.</summary>
    private IEnumerable<string> Glance(KnowledgeSettings knowledge, string typeId) =>
        Truth is { } truth && knowledge.Looking is { } looking
            ? looking.VisibleProperties.Where(p => truth.HasProperty(typeId, p)).Take(MaxPerception)
            : [];

    private void SubmitQuestions(World world, Inhabitant inhabitant)
    {
        if (_knowledge is not { NightQuestions: { } night } knowledge)
        {
            return;
        }

        var asked = 0;
        var skipped = 0;
        foreach (var question in _decisions.Open.Where(q => q.InhabitantId == inhabitant.Id && q.Subject.StartsWith(TypeSubject, StringComparison.Ordinal)).ToList())
        {
            if (night.Questions.FirstOrDefault(n => string.Equals(n.Trigger.ToString(), question.Kind, StringComparison.OrdinalIgnoreCase)) is not { } entry
                || _questionJobs.Values.Any(j => j.QuestionId == question.Id))
            {
                continue;
            }

            var typeId = question.Subject[TypeSubject.Length..];
            var noticedNothing = !inhabitant.Beliefs.About(typeId).Any();
            if (noticedNothing && (night.AskWithoutNotice != true || !Glance(knowledge, typeId).Any()))
            {
                // What it notices about the thing is among its beliefs (seen by looking): none at all = nothing to guess from.
                // D999: asked without a notice, the thing must at least show something at a glance - otherwise the model
                // guesses from memories of other things (#227).
                skipped++;
                continue;
            }

            asked++;
            var job = NewJob(world, inhabitant, JobKind.Question, "/question", night.JobPriority);
            job.Payload = new JobPayload
            {
                Question = new QuestionPayload
                {
                    Question = Enum.Parse<QuestionPayloadQuestion>(entry.Question.ToString()),
                    Subject = new Subject { Kind = SubjectKind.Type, Id = typeId },

                    // What the asker calls the kind of thing it asks about (an invention it knows something of).
                    Label = _inhabitants.TypeLabelOf(inhabitant, typeId),
                    Inhabitant = InhabitantStates.Asking(inhabitant, knowledge, typeId, _inhabitants.LabelOf, _inhabitants.TypeLabelOf),

                    // What it has seen of the thing by looking (source seen). D971: one it knows nothing about at all
                    // it still saw when it wondered - what that shows at a glance (the visible properties of the looking
                    // data the thing has), never more than a look would show.
                    Perception = noticedNothing ? [.. Glance(knowledge, typeId)] : [.. inhabitant.Beliefs.About(typeId)
                        .Where(b => b.Belief.Value && b.Belief.Source == KnowledgeSources.Seen)
                        .Select(b => b.PropertyId)
                        .Take(MaxPerception)],
                },
            };
            Send(world, job);
            _questionJobs[job.RequestId] = new QuestionJob(job.RequestId, inhabitant.Id, world.Tick, question.Id, typeId);
        }

        if (asked + skipped > 0)
        {
            _inhabitants.Report(new WorldEvent(NightQuestions, world.Tick, inhabitant.Id, null, null) { Detail = string.Create(CultureInfo.InvariantCulture, $"asked={asked},skipped={skipped}") });
        }
    }

    private GenerationJob NewJob(World world, Inhabitant inhabitant, JobKind kind, string purpose, int priority)
    {
        _counter++;
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id + purpose, _counter);
        return new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = world.Tick,
            Seed = DecisionIds.SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Kind = kind,
            Priority = priority,
            Inhabitant = inhabitant.Id,
            Object = null,
        };
    }

    private void Send(World world, GenerationJob job)
    {
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, job.RequestId, world.Tick, json, null));
        _port!.Submit(job.RequestId, json);
    }

    private void Apply(World world, PortMessage message)
    {
        _memoryJobs.TryGetValue(message.RequestId, out var memoryJob);
        _questionJobs.TryGetValue(message.RequestId, out var questionJob);
        if (memoryJob is null && questionJob is null)
        {
            // The router hands over only outcomes of open jobs; anything else is not ours to judge.
            return;
        }

        // Every outcome closes its job: nothing is asked twice under the same id.
        _memoryJobs.Remove(message.RequestId);
        _questionJobs.Remove(message.RequestId);

        var wanted = memoryJob is not null ? (memoryJob.Review ? JobKind.Review : JobKind.Memory) : JobKind.Question;
        JobStatus? status = null;
        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, ReplayNotes.OfError(message.Json)));
        }
        else
        {
            try
            {
                status = ContractSerializer.Deserialize<JobStatus>(message.Json);
            }
            catch (ContractViolationException)
            {
                Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.ContractViolation));
            }
        }

        if (status is not null && status.State == JobState.Failed && status.JobId == message.RequestId && status.Kind == wanted)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, status.Error?.Code.ToString()));
            status = null;
        }
        else if (status is not null && !(status.State == JobState.Done && status.JobId == message.RequestId && status.Kind == wanted
            && (memoryJob is not null ? (memoryJob.Review ? status.Result?.Review is not null : status.Result?.Memory is not null) : status.Result?.Question is not null)))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, NotFinal));
            status = null;
        }

        if (memoryJob is not null)
        {
            var memoryResult = status is null ? null
                : memoryJob.Review ? new MemoryResult { Summary = status.Result!.Review!.Summary, Kept = status.Result.Review.Kept }
                : status.Result!.Memory;
            var condensed = memoryResult is not null && Condense(world, memoryJob, memoryResult, message.Json);
            if (condensed && memoryJob.Review)
            {
                // The rest of a look back holds only with its memory: a refused summary refuses the night (asked anew).
                Reflect(world, memoryJob, status!.Result!.Review!, message.Json);
            }

            if (_inhabitants.ResolveTarget(memoryJob.InhabitantId)?.Inhabitant is { } inhabitant)
            {
                // A failed or refused job is not sent again tonight: the next night tries anew (no loop, B2487).
                Proceed(world, inhabitant, condensed);
            }
        }
        else if (status is not null)
        {
            Answer(world, questionJob!, status.Result!.Question!, message.Json);
        }
    }

    /// <summary>Checks a memory answer and takes it if it fits; false when it was refused (nothing changes).</summary>
    private bool Condense(World world, MemoryJob job, MemoryResult result, string json)
    {
        var inhabitant = _inhabitants.ResolveTarget(job.InhabitantId)?.Inhabitant;
        if (inhabitant is null)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, job.RequestId, world.Tick, json, ReplayNotes.QuestionClosed));
            return false;
        }

        var sentTicks = job.Sent.Select(s => s.Tick).ToHashSet();
        var kept = result.Kept.ToHashSet();
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(result.Summary))
        {
            reasons.Add(SummaryEmpty);
        }

        // Characters as the contract counts them (code points), so the core and the service agree on the budget.
        if (result.Summary.EnumerateRunes().Count() > job.MaxChars)
        {
            reasons.Add(SummaryTooLong);
        }

        if (kept.Any(tick => !sentTicks.Contains(tick)))
        {
            reasons.Add(KeptNotSent);
        }

        if (job.Sent.Any(s => s.Formative && !kept.Contains(s.Tick)))
        {
            reasons.Add(FormativeDropped);
        }

        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, job.RequestId, world.Tick, json, reasons.Count == 0 ? null : Rejected + string.Join(',', reasons)));
        if (reasons.Count > 0)
        {
            return false;
        }

        inhabitant.Memory.Condense(result.Summary, job.Sent.Select(s => s.Seq).ToHashSet(), kept);
        return true;
    }

    /// <summary>Checks a question answer; its guesses become beliefs and settle the question, unless it did not understand.</summary>
    private void Answer(World world, QuestionJob job, QuestionResult result, string json)
    {
        var inhabitant = _inhabitants.ResolveTarget(job.InhabitantId)?.Inhabitant;
        if (inhabitant is null || _knowledge is not { } knowledge || !_decisions.Open.Any(q => q.Id == job.QuestionId))
        {
            // Settled another way in the meantime (the decider answered): a late guess changes nothing.
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, job.RequestId, world.Tick, json, ReplayNotes.QuestionClosed));
            return;
        }

        var reasons = new List<string>();
        if (result.Guesses.Any(g => !_properties.Contains(g.Property)))
        {
            reasons.Add(UnknownProperty);
        }

        if (result.Guesses.GroupBy(g => g.Property, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            reasons.Add(PropertyTwice);
        }

        if (reasons.Count > 0)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, job.RequestId, world.Tick, json, Rejected + string.Join(',', reasons)));
            return;
        }

        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, job.RequestId, world.Tick, json, result.Understood ? null : NotUnderstood));
        if (result.Thought is { } thought && _guard.Check(thought.Text).Allowed)
        {
            _inhabitants.Report(new WorldEvent(ThoughtEvent, world.Tick, inhabitant.Id, null, null) { Text = thought.Text });
        }

        if (!result.Understood)
        {
            // Not understanding is allowed (W1595): the question stays open and is asked again another night.
            return;
        }

        foreach (var guess in result.Guesses)
        {
            // A guess never replaces a belief the inhabitant already has (as with guesses from looking).
            if (inhabitant.Beliefs.TryGet(job.TypeId, guess.Property, out _))
            {
                continue;
            }

            inhabitant.Beliefs.Set(job.TypeId, guess.Property, guess.Value, Math.Min(guess.Confidence, knowledge.GuessedCap), KnowledgeSources.Guessed, null, world.Tick);
            _inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, inhabitant.Id, null, job.TypeId) { Detail = guess.Property + "/" + KnowledgeSources.Guessed });
        }

        _decisions.Settle(world, job.QuestionId, Guessed);
    }
}
