using Bloom.Catalog.Data;

namespace Bloom.Catalog.Domain;

/// <summary>Who may see and buy what (plan R11, spec §7): the one source for the public page, search, reviews and GetPricing.</summary>
public static class Visibility
{
    /// <summary>
    /// SQL twin of <see cref="IsPublic"/> for raw queries; the products table is aliased <c>p</c>. A launch stage of announced or
    /// preorder_open shows the product whatever its status; otherwise it must be released (or have no launch) and active.
    /// </summary>
    public const string PublicProductSql =
        "p.kind = 'physical' AND (p.launch_stage IN ('announced', 'preorder_open') " +
        "OR (COALESCE(p.launch_stage, 'released') = 'released' AND p.status = 'active'))";

    public static bool IsPublic(Product p) =>
        p.Kind == ProductKinds.Physical
        && (p.LaunchStage is LaunchStages.Announced or LaunchStages.PreorderOpen
            || ((p.LaunchStage ?? LaunchStages.Released) == LaunchStages.Released && p.Status == ProductStatuses.Active));

    /// <summary>A warranty plan listed on an eligible device's page: the plan and its SKU are active.</summary>
    public static bool IsShownPlan(Product plan, Sku sku) => plan.Status == ProductStatuses.Active && sku.Active;

    /// <summary>What GetPricing reports as <c>buyable</c>.</summary>
    public static bool IsBuyable(Product p, Sku s) =>
        s.Active && p.Status == ProductStatuses.Active && (p.LaunchStage ?? LaunchStages.Released) == LaunchStages.Released;
}
