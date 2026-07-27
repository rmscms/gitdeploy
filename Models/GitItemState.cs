namespace GitDeployPro.Models
{
    /// <summary>
    /// Working-tree overlay state for Direct Upload explorer rows (Tortoise-inspired).
    /// </summary>
    public enum GitItemState
    {
        None = 0,
        Clean = 1,
        Modified = 2,
        Untracked = 3,
        Ignored = 4,
        Conflicted = 5
    }
}
