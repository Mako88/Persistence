using Dapper.Contrib.Extensions;

namespace Persistence.Data.Entities;

[Table("Sources")]
public record SourceEntity : BaseEntity
{
    public required SourceType SourceType { get; set; }

    public string? Name { get; set; }
}

public enum SourceType
{
    System = 0,

    /// <summary>
    /// A digital peer — a mind with its own Persistence memory (formerly "RemotePeer"). Embodiment is
    /// orthogonal: a robot-embodied peer is still a digital peer that happens to have a body. The integer
    /// value is unchanged from the old name so existing databases need no migration. See ADR-0007.
    /// </summary>
    DigitalPeer = 1,

    /// <summary>A human peer — a person participating through a client (formerly "LocalPeer").</summary>
    HumanPeer = 2,

    DerivedFromFragments = 3,
}
