using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.7 (AC #2; AD-27): the production CSP is script-src 'self', so web/index.html must never
// carry an inline script. The pre-paint theme script is the external web/public/theme-init.js, a plain
// (blocking) script in <head> placed after the theme-color meta it rewrites.
public partial class IndexHtmlHasNoInlineScriptTests
{
    [GeneratedRegex(@"<script\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SrcAttribute();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEventHandler();

    [Fact]
    public void Index_html_has_no_inline_script_event_handler_attribute_or_javascript_url()
    {
        var html = File.ReadAllText(Path.Combine(FindRepoRoot(), "web", "index.html"));

        var inlineScripts = ScriptTag().Matches(html).Where(m => !SrcAttribute().IsMatch(m.Groups["attrs"].Value)).ToList();

        inlineScripts.ShouldBeEmpty("web/index.html must not contain an inline <script> (CSP script-src 'self').");
        InlineEventHandler().IsMatch(html).ShouldBeFalse("web/index.html must not contain on*= event handler attributes.");
        html.Contains("javascript:", StringComparison.OrdinalIgnoreCase).ShouldBeFalse("web/index.html must not contain javascript: URLs.");
    }

    [Fact]
    public void Theme_init_is_a_plain_blocking_script_in_head_after_the_theme_color_meta_and_before_the_module_script()
    {
        var root = FindRepoRoot();
        File.Exists(Path.Combine(root, "web", "public", "theme-init.js")).ShouldBeTrue("web/public/theme-init.js must exist.");
        var html = File.ReadAllText(Path.Combine(root, "web", "index.html"));

        var themeInit = ScriptTag().Matches(html).SingleOrDefault(m => m.Groups["attrs"].Value.Contains("/theme-init.js", StringComparison.Ordinal));
        themeInit.ShouldNotBeNull("web/index.html must load /theme-init.js.");
        var attrs = themeInit.Groups["attrs"].Value;
        attrs.ShouldNotContain("type=", Case.Insensitive, "theme-init.js must be a classic script, not a module.");
        attrs.ShouldNotContain("async", Case.Insensitive);
        attrs.ShouldNotContain("defer", Case.Insensitive);

        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var themeColorMeta = html.IndexOf("name=\"theme-color\"", StringComparison.Ordinal);
        var moduleScript = html.IndexOf("type=\"module\"", StringComparison.Ordinal);
        headEnd.ShouldBeGreaterThanOrEqualTo(0, "web/index.html must have a </head>.");
        themeColorMeta.ShouldBeGreaterThanOrEqualTo(0, "web/index.html must have the theme-color meta that theme-init.js rewrites.");
        themeInit.Index.ShouldBeLessThan(headEnd, "theme-init.js must be loaded in <head>.");
        themeInit.Index.ShouldBeGreaterThan(themeColorMeta, "theme-init.js reads the theme-color meta, so it must come after it.");
        themeInit.Index.ShouldBeLessThan(moduleScript, "theme-init.js must run before the app module script.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (EnergyTracker.sln) from test base directory.");
    }
}
