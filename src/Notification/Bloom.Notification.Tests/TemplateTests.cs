using Bloom.Notification.Templates;

namespace Bloom.Notification.Tests;

public class TemplateTests
{
    private const string Order = "BLM-7K2M9QXA";

    private static readonly TemplateRenderer Renderer = new(new NotificationOptions { StorefrontBaseUrl = "https://shop.test" });

    private static Dictionary<string, string> Vars(params (string Key, string Value)[] overrides)
    {
        var vars = new Dictionary<string, string>
        {
            ["purpose"] = "signup", ["code"] = "482913", ["role"] = "Support", ["inviteUrl"] = "https://staff.test/invite/abc",
            ["orderNumber"] = Order, ["name"] = "Alex", ["reason"] = "customer", ["trackingNumber"] = "1Z999AA10123456784",
            ["kind"] = "lost", ["labelUrl"] = "https://labels.test/l/1", ["refundAmount"] = "$12.50",
        };
        foreach (var (key, value) in overrides) vars[key] = value;
        return vars;
    }

    public static TheoryData<string> AllTypes() => [.. NotificationTypes.All];

    public static TheoryData<string> InAppTypes() => [.. NotificationTypes.InApp];

    [Theory, MemberData(nameof(AllTypes))]
    public void Every_type_renders_every_part(string type)
    {
        var (subject, html, text) = Renderer.RenderEmail(type, Vars());
        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.False(string.IsNullOrWhiteSpace(html));
        Assert.Contains("<p>", html); // literal template markup is not encoded
        Assert.False(string.IsNullOrWhiteSpace(text));
        if (NotificationTypes.InApp.Contains(type))
        {
            var (title, body) = Renderer.RenderInApp(type, Vars());
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(body));
        }
        else Assert.ThrowsAny<Exception>(() => Renderer.RenderInApp(type, Vars()));
    }

    [Fact]
    public void Markup_is_escaped_in_html_only()
    {
        var vars = Vars(("name", "<b>Al & \"Bo\"</b>"), ("orderNumber", "<script>1</script>"), ("trackingNumber", "<script>2</script>"));
        var (subject, html, text) = Renderer.RenderEmail(NotificationTypes.OrderShipped, vars);
        var (title, body) = Renderer.RenderInApp(NotificationTypes.OrderShipped, vars);

        Assert.Contains("&lt;b&gt;Al &amp; &quot;Bo&quot;&lt;/b&gt;", html);
        Assert.Contains("&lt;script&gt;1&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;2&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("<b>Al & \"Bo\"</b>", text);
        Assert.Contains("<script>1</script>", subject);
        Assert.Contains("<script>2</script>", text);
        Assert.Contains("<script>1</script>", title + body);
        Assert.Contains("<script>2</script>", body);
    }

    [Theory, MemberData(nameof(AllTypes))]
    public void Emails_have_no_images(string type) =>
        Assert.DoesNotContain("<img", Renderer.RenderEmail(type, Vars()).Html, StringComparison.OrdinalIgnoreCase);

    [Theory, MemberData(nameof(InAppTypes))]
    public void Order_emails_link_the_order(string type)
    {
        var (_, html, text) = Renderer.RenderEmail(type, Vars());
        Assert.Contains($"https://shop.test/orders/{Order}", html);
        Assert.Contains($"https://shop.test/orders/{Order}", text);
        Assert.Equal($"/orders/{Order}", TemplateRenderer.InAppLink(Order));
    }

    [Fact]
    public void Order_url_has_no_doubled_slash()
    {
        var renderer = new TemplateRenderer(new NotificationOptions { StorefrontBaseUrl = "https://shop.test/" });
        Assert.Contains($"https://shop.test/orders/{Order}", renderer.RenderEmail(NotificationTypes.OrderConfirmed, Vars()).Text);
    }

    [Fact]
    public void Invitation_links_the_invite_and_code_email_has_no_order_link()
    {
        var (_, html, text) = Renderer.RenderEmail(NotificationTypes.StaffInvitation, Vars());
        Assert.Contains("https://staff.test/invite/abc", html);
        Assert.Contains("https://staff.test/invite/abc", text);

        var (_, codeHtml, codeText) = Renderer.RenderEmail(NotificationTypes.AccountCode, Vars());
        Assert.DoesNotContain("/orders/", codeHtml + codeText);
        Assert.DoesNotContain("http", codeHtml + codeText);
    }

    [Fact]
    public void Cancelled_text_differs_per_reason()
    {
        var generic = Renderer.RenderEmail(NotificationTypes.OrderCancelled, Vars(("reason", "other"))).Text;
        var texts = new[] { "customer", "support", "checkout_expired", "late_payment", "checkout_failed" }
            .Select(r => Renderer.RenderEmail(NotificationTypes.OrderCancelled, Vars(("reason", r))).Text).ToList();
        Assert.Equal(5, texts.Distinct().Count());
        Assert.DoesNotContain(generic, texts);

        var genericBody = Renderer.RenderInApp(NotificationTypes.OrderCancelled, Vars(("reason", "other"))).Body;
        var bodies = new[] { "customer", "support", "checkout_expired", "late_payment", "checkout_failed" }
            .Select(r => Renderer.RenderInApp(NotificationTypes.OrderCancelled, Vars(("reason", r))).Body).ToList();
        Assert.Equal(5, bodies.Distinct().Count());
        Assert.DoesNotContain(genericBody, bodies);
    }

    [Fact]
    public void Delivery_failed_text_differs_per_kind()
    {
        var texts = new[] { "returned_to_sender", "lost" }
            .Select(k => Renderer.RenderEmail(NotificationTypes.OrderDeliveryFailed, Vars(("kind", k))).Text).ToList();
        Assert.Equal(2, texts.Distinct().Count());

        var genericBody = Renderer.RenderInApp(NotificationTypes.OrderDeliveryFailed, Vars(("kind", "other"))).Body;
        var bodies = new[] { "returned_to_sender", "lost" }
            .Select(k => Renderer.RenderInApp(NotificationTypes.OrderDeliveryFailed, Vars(("kind", k))).Body).ToList();
        Assert.Equal(2, bodies.Distinct().Count());
        Assert.DoesNotContain(genericBody, bodies);
    }

    [Fact]
    public void Code_text_differs_per_purpose_and_shows_the_code()
    {
        var parts = new[] { "signup", "reset_password", "change_email", "other" }
            .Select(p => Renderer.RenderEmail(NotificationTypes.AccountCode, Vars(("purpose", p)))).ToList();
        Assert.Equal(4, parts.Select(p => p.Text).Distinct().Count());
        Assert.All(parts, p =>
        {
            Assert.Contains("482913", p.Html);
            Assert.Contains("482913", p.Text);
        });
        Assert.Contains("your Bloom code", parts[3].Text);
    }

    [Fact]
    public void Missing_variable_throws()
    {
        var vars = Vars();
        vars.Remove("orderNumber");
        Assert.ThrowsAny<Exception>(() => Renderer.RenderEmail(NotificationTypes.OrderConfirmed, vars));
    }

    [Theory, InlineData(123456, "$1,234.56"), InlineData(5, "$0.05"), InlineData(0, "$0.00")]
    public void Money_formats_us_dollars(long cents, string text) => Assert.Equal(text, Money.Format(cents));
}
