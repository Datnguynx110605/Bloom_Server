using System.Net;
using System.Reflection;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace Bloom.Notification.Templates;

/// <summary>
/// Renders the embedded Scriban templates (plan N15). Each <c>Templates/&lt;type&gt;.sbn</c> is split into sections by lines
/// <c>--- name</c> and parsed once, here. HTML sections encode every <c>{{ }}</c> output; the others render raw.
/// </summary>
public sealed class TemplateRenderer
{
    private readonly string _storefront;
    private readonly Dictionary<string, Dictionary<string, Template>> _templates = [];

    public TemplateRenderer(NotificationOptions options)
    {
        _storefront = options.StorefrontBaseUrl.TrimEnd('/');
        var assembly = typeof(TemplateRenderer).Assembly;
        foreach (var type in NotificationTypes.All)
        {
            using var stream = assembly.GetManifestResourceStream($"{type}.sbn") ?? throw new InvalidOperationException($"Template {type}.sbn is not embedded.");
            _templates[type] = Sections(new StreamReader(stream).ReadToEnd(), type);
        }
    }

    public static string InAppLink(string orderNumber) => $"/orders/{orderNumber}";

    public (string Subject, string Html, string Text) RenderEmail(string type, IReadOnlyDictionary<string, string> vars)
    {
        var sections = _templates[type];
        var all = new Dictionary<string, string>(vars);
        if (vars.TryGetValue("orderNumber", out var orderNumber)) all["orderUrl"] = $"{_storefront}/orders/{orderNumber}";
        return (Render(sections["subject"], all, html: false), Render(sections["html"], all, html: true), Render(sections["text"], all, html: false));
    }

    public (string Title, string Body) RenderInApp(string type, IReadOnlyDictionary<string, string> vars)
    {
        if (!NotificationTypes.InApp.Contains(type)) throw new ArgumentException($"'{type}' has no in-app notification.", nameof(type));
        var sections = _templates[type];
        return (Render(sections["title"], vars, html: false), Render(sections["body"], vars, html: false));
    }

    private static string Render(Template template, IReadOnlyDictionary<string, string> vars, bool html)
    {
        var model = new ScriptObject();
        foreach (var (key, value) in vars) model[key] = value;
        var context = html ? new HtmlContext() : new TemplateContext();
        context.StrictVariables = true;
        context.PushGlobal(model);
        return template.Render(context).Trim();
    }

    private static Dictionary<string, Template> Sections(string source, string type)
    {
        var sections = new Dictionary<string, Template>();
        string? name = null;
        var body = new List<string>();
        void Flush()
        {
            if (name is null) return;
            var template = Template.Parse(string.Join('\n', body), $"{type}.sbn:{name}");
            if (template.HasErrors) throw new InvalidOperationException($"Template {type}.sbn section '{name}': {string.Join("; ", template.Messages)}");
            sections[name] = template;
        }
        foreach (var line in source.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("--- "))
            {
                Flush();
                name = line[4..].Trim();
                body.Clear();
            }
            else body.Add(line);
        }
        Flush();
        var required = NotificationTypes.InApp.Contains(type) ? new[] { "subject", "html", "text", "title", "body" } : ["subject", "html", "text"];
        foreach (var section in required)
            if (!sections.ContainsKey(section)) throw new InvalidOperationException($"Template {type}.sbn has no '{section}' section.");
        return sections;
    }

    /// <summary>Encodes every value a <c>{{ }}</c> writes; literal template text goes through <c>Write(string)</c> and stays as is.</summary>
    private sealed class HtmlContext : TemplateContext
    {
        public override TemplateContext Write(SourceSpan span, object? textAsObject)
        {
            if (textAsObject is null) return this;
            return Write(WebUtility.HtmlEncode(ObjectToString(textAsObject)));
        }
    }
}
