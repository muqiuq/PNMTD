using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>The lounge's own state (docs/lounge-plan.md §3.3, §12): stopped until an admin starts it, then asleep, awake or speaking.</summary>
public enum LoungeState { Stopped, Asleep, Awake, Speaking, Paused }

/// <summary>
/// A line of the lounge (docs/lounge-plan.md §3.2, §12): one conversation, the same for every signed-in user. Cookie's and Seraph's
/// messages, people's messages and comments, and the room's own lines (closed, asleep, out of budget). Stored as people read it,
/// with names; a removed one keeps its row for the audit.
/// </summary>
[Table("lounge_messages")]
[NotAudited("the lounge's conversation; removing a message is audited as LoungeMessageDeleted")]
public class LoungeMessageEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>cookie, seraph, person or system.</summary>
    [MaxLength(16)]
    public string From { get; set; } = "cookie";

    /// <summary>A person's: message (the voices answer it) or comment (they take it up, §9.3). The voices' and the room's are messages.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "message";

    public Guid? UserId { get; set; }

    public string Text { get; set; } = "";

    public long? SceneId { get; set; }

    /// <summary>The person's message a voice answered.</summary>
    public long? ReplyToId { get; set; }

    /// <summary>The message a comment is on; none for a comment on the conversation.</summary>
    public long? OnId { get; set; }

    /// <summary>The voice's message that took a comment up (§9.3): each comment is given to a voice once.</summary>
    public long? TakenById { get; set; }

    /// <summary>The areas of what the voice read for it: the ground's and the tools' (§3.2).</summary>
    public List<string> Areas { get; set; } = [];

    [MaxLength(200)]
    public string? Model { get; set; }

    public decimal Cost { get; set; }

    public int ToolCalls { get; set; }

    /// <summary>A case the message raised (§8.1).</summary>
    public Guid? CaseId { get; set; }

    /// <summary>An e-mail the message proposed or sent, in Cookie's decision log (§8.2).</summary>
    public long? DecisionId { get; set; }

    /// <summary>Jev flagged a person's message or comment (§9.2): it stays in the room, and never reaches a model.</summary>
    [MaxLength(300)]
    public string? RefusedWhy { get; set; }

    /// <summary>A person's message waits for its answer until a voice gives one (§9.2).</summary>
    public bool Answered { get; set; }

    /// <summary>A voice's message it marked important, or that raised a case (§4.3): it may be long, and the room shows it.</summary>
    public bool Important { get; set; }

    public DateTime? DeletedAt { get; set; }

    [MaxLength(200)]
    public string? DeletedBy { get; set; }
}

/// <summary>A scene of the lounge, or a night (docs/lounge-plan.md §5.2, §7.3, §12): what it was about, who opened it, what it cost.</summary>
[Table("lounge_scenes")]
[NotAudited("the lounge's scenes, its log for Admin → Cookie → Lounge")]
public class LoungeSceneEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    /// <summary>scene, morning, evening, reply or night.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "scene";

    /// <summary>cookie or seraph.</summary>
    [MaxLength(16)]
    public string Opener { get; set; } = "cookie";

    /// <summary>What the scene was about: the seed's key (case:…, ticket:…, memory:…, conviction:…, comment:…), or null for free talk.</summary>
    [MaxLength(100)]
    public string? SeedKey { get; set; }

    /// <summary>The seed as one line, for the tab and the night.</summary>
    [MaxLength(300)]
    public string? Seed { get; set; }

    public bool Disagree { get; set; }

    public int PlannedLength { get; set; }

    public int Messages { get; set; }

    public int ToolCalls { get; set; }

    public decimal Cost { get; set; }

    /// <summary>talked, passed, failed, stopped, or for a night slept.</summary>
    [MaxLength(16)]
    public string Outcome { get; set; } = "";

    [MaxLength(64)]
    public string? GroundHash { get; set; }

    /// <summary>The next voice speaks within 2 minutes of the last scene (§5.2): it is told to move on, not to start over.</summary>
    public bool MoveOn { get; set; }
}

/// <summary>A step of a scene (docs/lounge-plan.md §12): the ground, reasoning, a tool call, a refusal, a case, an e-mail, a memory, an error.</summary>
[Table("lounge_steps")]
[NotAudited("the lounge's log, the trace's twin, for Cookie: Manage")]
public class LoungeStepEntity
{
    [Key]
    public long Id { get; set; }

    public long SceneId { get; set; }

    public long? MessageId { get; set; }

    public DateTime At { get; set; }

    /// <summary>ground, reasoning, tool, refused, case, email, memory, opinion, error.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "";

    [MaxLength(300)]
    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public List<string> Areas { get; set; } = [];
}

/// <summary>
/// One of a voice's memories (docs/lounge-plan.md §7): one sentence, stored masked, whose strength fades with a half-life unless it is a
/// keepsake. The strength now is computed from <see cref="Strength0"/> and <see cref="RefreshedAt"/>.
/// </summary>
[Table("lounge_memories")]
[NotAudited("the voices' memories; deleting them is audited as LoungeMemoryDeleted and LoungeForgotten")]
public class LoungeMemoryEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>cookie or seraph.</summary>
    [MaxLength(16)]
    public string Voice { get; set; } = "cookie";

    /// <summary>The sentence as the voice wrote it, placeholders and all (§7.1).</summary>
    [MaxLength(300)]
    public string Text { get; set; } = "";

    /// <summary>moment, person, joke or lesson.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "moment";

    /// <summary>One of the voice's three that never fade (§7.4).</summary>
    public bool Keepsake { get; set; }

    public double Strength0 { get; set; } = 1;

    public DateTime RefreshedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public int Recalls { get; set; }

    public long? SourceMessageId { get; set; }

    /// <summary>The PNMT users it names (§9.4): an opt-out deletes it.</summary>
    public List<Guid> People { get; set; } = [];

    public List<string> Areas { get; set; } = [];
}

/// <summary>A move of a voice's position on a conviction (docs/lounge-plan.md §6.2): before, after, why. The current ones are in the runtime.</summary>
[Table("lounge_opinions")]
[NotAudited("the convictions' history; resetting the positions is audited as LoungePositionsReset")]
public class LoungeOpinionEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime At { get; set; }

    [MaxLength(32)]
    public string Conviction { get; set; } = "";

    [MaxLength(16)]
    public string Voice { get; set; } = "cookie";

    public double Before { get; set; }

    public double After { get; set; }

    [MaxLength(300)]
    public string Why { get; set; } = "";
}

/// <summary>The lounge's running state (docs/lounge-plan.md §3.3, §12), in one row, created like <c>cookie_runtime</c>.</summary>
[Table("lounge_runtime")]
[NotAudited("the lounge's running state; starting and stopping it are in the audit log")]
public class LoungeRuntimeEntity
{
    public const int Id = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int SingletonId { get; set; } = Id;

    /// <summary>An admin started it; it resumes after a restart.</summary>
    public bool Running { get; set; }

    public DateTime? StartedAt { get; set; }

    [MaxLength(200)]
    public string? StartedBy { get; set; }

    public DateTime? StoppedAt { get; set; }

    [MaxLength(200)]
    public string? StoppedBy { get; set; }

    public LoungeState State { get; set; } = LoungeState.Stopped;

    /// <summary>cookie or seraph while one is writing: the room shows <i>Cookie is typing…</i>.</summary>
    [MaxLength(16)]
    public string? Speaking { get; set; }

    [MaxLength(500)]
    public string? StateReason { get; set; }

    /// <summary>When the next message, scene or wake-up is planned.</summary>
    public DateTime? NextAt { get; set; }

    /// <summary>The scene that is running, until its last message.</summary>
    public long? SceneId { get; set; }

    /// <summary>The last night (§7.3): one per awake period.</summary>
    public DateTime? LastNightAt { get; set; }

    /// <summary>When the last awake period began, so that a night and a morning belong to it.</summary>
    public DateTime? AwakeSince { get; set; }

    /// <summary>The positions now (§6): conviction → voice → position, as JSON.</summary>
    public string Positions { get; set; } = "{}";

    /// <summary>Calls that failed in a row; at 3, the lounge sleeps 30 minutes.</summary>
    public int Failures { get; set; }

    public DateTime? PausedUntil { get; set; }

    /// <summary><i>Put to sleep</i> (§3.3): asleep until the next awake period, which starts after this.</summary>
    public DateTime? SleptAt { get; set; }

    [MaxLength(64)]
    public string? GroundHash { get; set; }

    /// <summary>The last error, for the tab.</summary>
    [MaxLength(500)]
    public string? LastError { get; set; }

    public DateTime? LastErrorAt { get; set; }
}
