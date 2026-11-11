namespace ClaudiaKerman.Core.Tools
{
    /// <summary>Where the last aiming block call aimed the landing (#892), for a block test, which has no Mission; a Mission keeps its own, in its save. A reentry takes it, so it never trims toward an old flight's target.</summary>
    public static class BlockTestAim
    {
        public static LandingTarget Landing { get; private set; }

        public static void Record(LandingTarget target)
        {
            Landing = target;
        }

        /// <summary>The reentry has read it: the next one starts with none.</summary>
        public static void Clear()
        {
            Landing = null;
        }
    }
}
