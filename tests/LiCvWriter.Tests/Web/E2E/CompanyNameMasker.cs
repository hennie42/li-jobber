using System.Text.Json;
using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public static class CompanyNameMasker
{
    public static Task InstallAsync(IBrowserContext context, IReadOnlyList<string> companyNames)
        => context.AddInitScriptAsync(BuildInstallScript(companyNames));

    public static Task StartAsync(IPage page, IReadOnlyList<string> companyNames)
        => page.EvaluateAsync(
            "names => window.__liCvWriterStartCompanyMask && window.__liCvWriterStartCompanyMask(names)",
            ExpandMaskTerms(companyNames));

    public static Task ApplyAsync(IPage page)
        => page.EvaluateAsync("() => window.__liCvWriterMaskCompanyNames && window.__liCvWriterMaskCompanyNames()");

    private static string BuildInstallScript(IReadOnlyList<string> companyNames)
        => InstallScriptTemplate.Replace("__COMPANY_NAMES__", JsonSerializer.Serialize(ExpandMaskTerms(companyNames)), StringComparison.Ordinal);

    private static IReadOnlyList<string> ExpandMaskTerms(IReadOnlyList<string> companyNames)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var companyName in companyNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            terms.Add(companyName.Trim());
            terms.Add(string.Join('-', SplitCompanyName(companyName).Select(token => token.ToLowerInvariant())));

            foreach (var token in SplitCompanyName(companyName))
            {
                if (token.Length > 3 && !GenericCompanyTokens.Contains(token))
                {
                    terms.Add(token);
                }
            }
        }

        return terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .OrderByDescending(term => term.Length)
            .ToArray();
    }

    private static IEnumerable<string> SplitCompanyName(string companyName)
        => companyName.Split([' ', '-', '_', '.', ',', '/', '\\', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly HashSet<string> GenericCompanyTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "company",
        "corporation",
        "corp",
        "demo",
        "group",
        "inc",
        "labs",
        "limited",
        "llc",
        "ltd",
        "services",
        "solutions",
        "systems",
        "technologies",
        "technology",
        "works"
    };

    private const string InstallScriptTemplate =
        """
        (() => {
            window.__liCvWriterStartCompanyMask = names => {
                window.__liCvWriterCompanyNames = names || [];
                window.__liCvWriterMaskCompanyNames = () => {
                    const terms = window.__liCvWriterCompanyNames || [];
                    if (!terms.length || !document.body) return;
                    const escaped = terms.map(term => term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
                    const pattern = new RegExp(`(${escaped.join('|')})`, 'gi');
                    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
                        acceptNode(node) {
                            if (!node.nodeValue || !pattern.test(node.nodeValue)) return NodeFilter.FILTER_REJECT;
                            pattern.lastIndex = 0;
                            const parent = node.parentElement;
                            if (!parent || parent.closest('script,style,[data-company-mask]')) return NodeFilter.FILTER_REJECT;
                            return NodeFilter.FILTER_ACCEPT;
                        }
                    });
                    const nodes = [];
                    while (walker.nextNode()) nodes.push(walker.currentNode);
                    for (const node of nodes) {
                        const fragment = document.createDocumentFragment();
                        const parts = node.nodeValue.split(pattern);
                        for (const part of parts) {
                            if (!part) continue;
                            pattern.lastIndex = 0;
                            if (pattern.test(part)) {
                                const span = document.createElement('span');
                                span.dataset.companyMask = 'true';
                                span.style.filter = 'blur(6px)';
                                span.style.display = 'inline-block';
                                span.style.userSelect = 'none';
                                span.textContent = part;
                                fragment.appendChild(span);
                            } else {
                                fragment.appendChild(document.createTextNode(part));
                            }
                        }
                        node.parentNode.replaceChild(fragment, node);
                    }
                };
                window.clearInterval(window.__liCvWriterCompanyMaskInterval);
                window.__liCvWriterCompanyMaskInterval = window.setInterval(window.__liCvWriterMaskCompanyNames, 250);
                window.__liCvWriterMaskCompanyNames();
            };

            const install = () => window.__liCvWriterStartCompanyMask(__COMPANY_NAMES__);
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', install, { once: true });
            } else {
                install();
            }
        })();
        """;
}
