using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Errors;
using Grpc.Core;

namespace Bloom.Order.Tests;

public class PricingTests
{
    private const int Bp = 825;

    private static PricedLine Product(int no, long unit, int qty = 1, Guid? product = null, Guid? category = null) =>
        new(no, LineKinds.Product, Guid.NewGuid(), product ?? Guid.NewGuid(), category ?? Guid.NewGuid(), "p", false, qty, null, null, unit);

    private static PricedLine Plan(int no, long unit, int qty = 1) =>
        new(no, LineKinds.WarrantyPlan, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "w", false, qty, 1, 12, unit);

    private static CouponTerms Coupon(string type, int value, string scope = CouponScopes.Order, long? min = null, params (string, Guid)[] items) =>
        new(type, value, scope, items, min);

    private static Totals Compute(PricedLine[] lines, CouponTerms? coupon = null, bool launch = false, long shipping = 0) =>
        Pricing.Compute(lines, coupon, launch, Bp, shipping);

    private static void Fails(string reason, Action call) => Assert.Equal(reason, Rpc.Info(Assert.Throws<RpcException>(call)).Reason);

    [Fact]
    public void O06_fixed_1000_over_333_333_334_splits_exactly()
    {
        var t = Compute([Product(1, 333), Product(2, 333), Product(3, 334)], Coupon(CouponTypes.Fixed, 1000));
        Assert.Equal([333L, 333, 334], t.Lines.Select(l => l.DiscountCents));
        Assert.Equal(1000, t.DiscountCents);
    }

    [Fact]
    public void Largest_remainder_ties_go_to_the_lower_line()
    {
        var t = Compute([Product(1, 1000), Product(2, 1000), Product(3, 1000)], Coupon(CouponTypes.Fixed, 100));
        Assert.Equal([34L, 33, 33], t.Lines.Select(l => l.DiscountCents));
    }

    [Fact]
    public void O07_half_cent_tax_rounds_up()
    {
        Assert.Equal(83, Compute([Product(1, 1000)]).Lines[0].TaxCents);
        Assert.Equal(83, Compute([Product(1, 1001)]).Lines[0].TaxCents);
    }

    [Fact]
    public void Percent_discount_floors()
    {
        var t = Compute([Product(1, 999)], Coupon(CouponTypes.Percent, 15));
        Assert.Equal(149, t.DiscountCents);
        Assert.Equal(149, t.Lines[0].DiscountCents);
    }

    [Fact]
    public void Fixed_coupon_is_capped_at_the_eligible_total()
    {
        var t = Compute([Product(1, 1000, 3)], Coupon(CouponTypes.Fixed, 5000));
        Assert.Equal(3000, t.DiscountCents);
        Assert.Equal(0, t.Lines[0].PaidCents);
        Assert.Equal(0, t.TotalCents);
    }

    [Fact]
    public void Plans_get_no_discount_and_no_tax()
    {
        var t = Compute([Product(1, 1000), Plan(2, 500, 2)], Coupon(CouponTypes.Percent, 10), shipping: 700);
        Assert.Equal(new LineAmounts(2, 0, 0, 1000), t.Lines[1]);
        Assert.Equal(100, t.DiscountCents);
        Assert.Equal(new LineAmounts(1, 100, 74, 974), t.Lines[0]); // (900 * 825 + 5000) / 10000 = 74
        Assert.Equal(2000, t.SubtotalCents);
        Assert.Equal(974 + 1000 + 700, t.TotalCents);
    }

    [Fact]
    public void Selected_scope_matches_product_or_category()
    {
        Guid p = Guid.NewGuid(), c = Guid.NewGuid();
        var lines = new[] { Product(1, 1000, product: p), Product(2, 1000, category: c), Product(3, 1000) };
        var coupon = Coupon(CouponTypes.Fixed, 400, CouponScopes.Selected, null, (TargetTypes.Product, p), (TargetTypes.Category, c));
        var t = Compute(lines, coupon);
        Assert.Equal([200L, 200, 0], t.Lines.Select(l => l.DiscountCents));
    }

    [Fact]
    public void Min_spend_counts_eligible_lines_only()
    {
        var p = Guid.NewGuid();
        var lines = new[] { Product(1, 1000, product: p), Product(2, 5000) };
        var coupon = Coupon(CouponTypes.Percent, 10, CouponScopes.Selected, 2000, (TargetTypes.Product, p));
        Fails(OrderErrors.CouponMinSpend, () => Compute(lines, coupon));
        Assert.Equal(100, Compute(lines, coupon with { MinSpendCents = 1000 }).DiscountCents);
    }

    [Fact]
    public void Launch_order_with_a_coupon_is_COUPON_NOT_APPLICABLE()
    {
        Fails(OrderErrors.CouponNotApplicable, () => Compute([Product(1, 1000)], Coupon(CouponTypes.Percent, 10), launch: true));
        Fails(OrderErrors.CouponNotApplicable, () => Compute([Plan(1, 1000)], Coupon(CouponTypes.Percent, 10)));
        Assert.Equal(0, Compute([Product(1, 1000)], null, launch: true).DiscountCents);
    }

    [Fact]
    public void Total_is_the_sum_of_paid_plus_shipping()
    {
        var rnd = new Random(20261107);
        for (var i = 0; i < 200; i++)
        {
            var products = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
            var lines = Enumerable.Range(1, rnd.Next(1, 8))
                .Select(no => rnd.Next(5) == 0 ? Plan(no, rnd.Next(1, 20000), rnd.Next(1, 4)) : Product(no, rnd.Next(1, 20000), rnd.Next(1, 4), products[rnd.Next(3)]))
                .ToArray();
            var coupon = rnd.Next(4) == 0 ? null : rnd.Next(2) == 0
                ? Coupon(CouponTypes.Percent, rnd.Next(1, 101), rnd.Next(2) == 0 ? CouponScopes.Order : CouponScopes.Selected, null, (TargetTypes.Product, products[rnd.Next(3)]))
                : Coupon(CouponTypes.Fixed, rnd.Next(1, 60000), rnd.Next(2) == 0 ? CouponScopes.Order : CouponScopes.Selected, null, (TargetTypes.Product, products[rnd.Next(3)]));
            var shipping = rnd.Next(2) * 1500L;

            Totals t;
            try { t = Compute(lines, coupon, false, shipping); }
            catch (RpcException) { continue; } // no eligible line: a rejected coupon, nothing to sum

            Assert.Equal(t.Lines.Sum(l => l.PaidCents) + shipping, t.TotalCents);
            Assert.Equal(t.DiscountCents, t.Lines.Sum(l => l.DiscountCents));
            Assert.Equal(t.TaxCents, t.Lines.Sum(l => l.TaxCents));
            Assert.Equal(t.SubtotalCents - t.DiscountCents + t.TaxCents + shipping, t.TotalCents);
            Assert.All(lines, l => Assert.Equal(l.UnitPriceCents * l.Quantity - t.Lines.Single(a => a.LineNo == l.LineNo).DiscountCents + t.Lines.Single(a => a.LineNo == l.LineNo).TaxCents,
                t.Lines.Single(a => a.LineNo == l.LineNo).PaidCents));
        }
    }
}
