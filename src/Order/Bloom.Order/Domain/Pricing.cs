using Bloom.Order.Data;
using Bloom.Order.Errors;

namespace Bloom.Order.Domain;

public sealed record PricedLine(int LineNo, string Kind, Guid SkuId, Guid ProductId, Guid CategoryId, string Name, bool Serialized, int Quantity, int? ParentLineNo, int? CoverageMonths, long UnitPriceCents);

public sealed record CouponTerms(string Type, int Value, string Scope, IReadOnlyCollection<(string TargetType, Guid TargetId)> Items, long? MinSpendCents);

public sealed record LineAmounts(int LineNo, long DiscountCents, long TaxCents, long PaidCents);

public sealed record Totals(long SubtotalCents, long DiscountCents, long TaxCents, long ShippingCents, long TotalCents, IReadOnlyList<LineAmounts> Lines);

/// <summary>03-order §6.3. Integer cents only; line amounts always add up to the total (OR-05).</summary>
public static class Pricing
{
    public static Totals Compute(IReadOnlyList<PricedLine> lines, CouponTerms? coupon, bool launchOrder, int taxRateBp, long shippingCents)
    {
        var gross = lines.ToDictionary(l => l.LineNo, l => l.UnitPriceCents * l.Quantity); // step 1
        var discount = new Dictionary<int, long>();

        if (coupon is not null)
        {
            var eligible = launchOrder ? [] : lines.Where(l => l.Kind == LineKinds.Product && InScope(coupon, l)).OrderBy(l => l.LineNo).ToList(); // step 2
            if (eligible.Count == 0)
                throw OrderErrors.Error(OrderErrors.CouponNotApplicable, "The coupon does not apply to this order.");

            var sum = eligible.Sum(l => gross[l.LineNo]);
            if (sum < (coupon.MinSpendCents ?? 0)) // step 3
                throw OrderErrors.Error(OrderErrors.CouponMinSpend, "The eligible items do not reach the coupon's minimum spend.");

            if (sum > 0) // all eligible lines free: every discount stays 0
            {
                var d = Math.Min(sum, coupon.Type == CouponTypes.Percent ? sum * coupon.Value / 100 : coupon.Value); // step 4
                foreach (var l in eligible) discount[l.LineNo] = d * gross[l.LineNo] / sum; // step 5: floors
                var leftover = d - discount.Values.Sum();
                foreach (var l in eligible.OrderByDescending(l => d * gross[l.LineNo] % sum).ThenBy(l => l.LineNo).Take((int)leftover))
                    discount[l.LineNo]++;
            }
        }

        var amounts = lines.Select(l =>
        {
            var disc = discount.GetValueOrDefault(l.LineNo);
            var net = gross[l.LineNo] - disc;
            var tax = l.Kind == LineKinds.Product ? (net * taxRateBp + 5000) / 10000 : 0; // step 6
            return new LineAmounts(l.LineNo, disc, tax, net + tax); // step 7
        }).ToList();

        return new Totals(gross.Values.Sum(), amounts.Sum(a => a.DiscountCents), amounts.Sum(a => a.TaxCents), shippingCents,
            amounts.Sum(a => a.PaidCents) + shippingCents, amounts); // step 8
    }

    private static bool InScope(CouponTerms c, PricedLine l) =>
        c.Scope == CouponScopes.Order
        || c.Items.Any(i => i.TargetType == TargetTypes.Product ? i.TargetId == l.ProductId : i.TargetId == l.CategoryId);
}
