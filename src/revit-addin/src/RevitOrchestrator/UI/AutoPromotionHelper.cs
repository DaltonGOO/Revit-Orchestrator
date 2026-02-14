using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// Deterministic heuristics for auto-promoting step arguments to workflow-level input parameters.
/// </summary>
public static class AutoPromotionHelper
{
    private static readonly HashSet<string> KnownPromotableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "level_name",
        "type_name",
        "wall_type",
        "height",
        "width",
        "depth",
        "offset",
        "phase_name",
        "view_name",
        "family_name",
        "diameter",
    };

    /// <summary>
    /// Returns true if the argument key is a known candidate for promotion.
    /// </summary>
    public static bool ShouldAutoPromote(string key)
    {
        return KnownPromotableKeys.Contains(key);
    }

    /// <summary>
    /// Find arg keys that appear in ALL steps with identical values (strong promotion candidates).
    /// Returns a dictionary of key → shared value.
    /// </summary>
    public static Dictionary<string, object?> FindSharedConstants(IReadOnlyList<WorkflowStep> steps)
    {
        if (steps.Count == 0)
            return new Dictionary<string, object?>();

        // Start with all keys from the first step
        var candidates = new Dictionary<string, object?>();
        foreach (var kvp in steps[0].Args)
        {
            candidates[kvp.Key] = kvp.Value;
        }

        // Intersect with subsequent steps
        for (int i = 1; i < steps.Count; i++)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in candidates)
            {
                if (!steps[i].Args.TryGetValue(kvp.Key, out var val) ||
                    !ValuesEqual(kvp.Value, val))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            foreach (var key in keysToRemove)
                candidates.Remove(key);
        }

        return candidates;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.ToString() == b.ToString();
    }
}
