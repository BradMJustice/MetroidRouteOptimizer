namespace MetroidRouteOptimizer
{
    /// <summary>
    /// All major powerups in Super Metroid
    /// </summary>
    public enum Powerup
    {
        Bombs,
        ChargeBeam,
        EnergyTank,
        GravitySuit,
        GrappleBeam,
        HighJumpBoots,
        IceBeam,
        Missiles,
        MorphBall,
        PlasmaBeam,
        PowerBomb,
        ReserveTank,
        ScrewAttack,
        SpaceJump,
        Spazer,
        SpeedBooster,
        SpringBall,
        SuperMissiles,
        VariaSuit,
        WaveBeam,
        XRayScope
    }

    /// <summary>
    /// Represents a requirement that can be satisfied by meeting any one of
    /// several "AND-groups" of powerups. For example, if we have:
    /// (SpeedBooster AND Bombs) OR (PowerBomb),
    /// then if the player has SpeedBooster and Bombs, or just PowerBomb,
    /// this requirement is fulfilled.
    /// </summary>
    public class Requirement
    {
        // Each inner list is an "AND" group; the full collection is an "OR" set.
        // For example:
        // [ [ SpeedBooster, Bombs ], [ PowerBomb ] ]
        public List<List<Powerup>> OrGroups { get; } = new();
    }

    /// <summary>
    /// A single collectible instance of a powerup, along with its requirements.
    /// </summary>
    public class CollectiblePowerup
    {
        public Powerup Powerup { get; set; }
        public Requirement Requirement { get; } = new();
    }

    public class Exit
    {
        public int DestinationScreenId { get; } = new();
        public Requirement Requirement { get; } = new();
    }

    public class Screen
    {
        public int Id { get; set; }
        public List<CollectiblePowerup> Powerups { get; } = new();
        public List<Exit> Exits { get; } = new();
    }

    public class GameState
    {
        public HashSet<Screen> VisitedScreens { get; } = new();

        // Keep track of how many copies of each powerup have been collected.
        public HashSet<Powerup> CollectedPowerups { get; } = new();
    }
}
