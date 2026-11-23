// A comprehensive example EMMD document, loadable from the Studio sidebar.
// It exercises the whole format: frontmatter, a scoped custom-CSS layer,
// a working TOC (heading ids come from rehypeHeadingIds), markdown + raw-HTML
// tables, task lists, callouts, details/summary, mark/kbd/sup, and code blocks.
// NOTE: code fences use ~~~ (tildes) so this file avoids escaped backticks.

export const EMMD_SAMPLE = {
  title: 'Website Replatform — Project Proposal',
  subtitle: 'Prepared for Acme Corporation',
  author: 'GenPrompt Consulting',
  theme: 'proposal',

  styleCss: `/* Document-scoped CSS — sanitised and applied only inside this document. */
.toc { background: rgba(131,58,180,.05); border: 1px solid rgba(131,58,180,.25); border-radius: 12px; padding: 14px 20px; }
.toc a { text-decoration: none; font-weight: 600; }
.toc li { margin: .2em 0; }

.callout { border-left: 4px solid #833ab4; background: rgba(131,58,180,.06); padding: 12px 16px; border-radius: 0 10px 10px 0; margin: 16px 0; }
.callout-warn { border-left-color: #b45309; background: rgba(180,83,9,.08); }

.tag { display: inline-block; padding: 2px 10px; border-radius: 999px; background: #42115f; color: #fff; font-size: .78em; margin: 0 4px 4px 0; }

.price th:last-child, .price td:last-child { text-align: right; font-variant-numeric: tabular-nums; }
.price .total td { font-weight: 700; border-top: 2px solid #42115f; background: rgba(131,58,180,.05); }

.sig-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 32px; margin-top: 28px; }
.sig { border-top: 1.5px solid #0f172a; padding-top: 6px; font-size: .88em; color: #475569; }

.fineprint { font-size: .8em; color: #64748b; }`,

  body: `<div class="toc">

**Contents**

1. [Executive summary](#executive-summary)
2. [Objectives and success metrics](#objectives-and-success-metrics)
3. [Scope of work](#scope-of-work)
4. [Timeline and milestones](#timeline-and-milestones)
5. [Investment](#investment)
6. [Team](#team)
7. [Assumptions and risks](#assumptions-and-risks)
8. [Acceptance](#acceptance)

</div>

## Executive summary

Acme's current site runs on a **legacy CMS** that takes *11 days on average* to publish a campaign page and cannot pass <mark>Core Web Vitals</mark>. This proposal replatforms acme.com onto a modern stack — headless CMS, edge rendering, and a reusable component library — in **12 weeks**, with content migration handled by script rather than by hand.

<div class="callout">
💡 <strong>The one-line pitch:</strong> ship campaign pages in hours instead of weeks, cut page weight by half, and hand marketing a self-serve publishing workflow.
</div>

## Objectives and success metrics

| Metric | Baseline | Target (90 days post-launch) | Owner |
|---|---|---|---|
| Campaign page turnaround | 11 days | **< 1 day** | Marketing Ops |
| Largest Contentful Paint (p75) | 4.9 s | **< 2.0 s** | Engineering |
| Organic sessions / month | 182 k | **230 k** | SEO |
| Publishing steps per page | 14 | **3** | Marketing Ops |
| CMS licence cost / year | $84 k | **$29 k** | Finance |

## Scope of work

### Phase 1 — Foundations

- [x] Stakeholder interviews and content audit *(completed during discovery)*
- [ ] Information architecture and URL map (with 301 strategy)
- [ ] Design tokens and component inventory
- [ ] Headless CMS setup: roles, workflows, preview environments

### Phase 2 — Build

- [ ] Component library (24 components, documented in Storybook)
- [ ] Page templates: home, product, campaign, article, careers
- [ ] Scripted migration of 640 pages, with redirect verification
- [ ] Search, analytics, and consent integration

### Phase 3 — Launch and handover

- [ ] Performance hardening and accessibility audit (WCAG 2.2 AA)
- [ ] Editor training (two workshops) and playbook
- [ ] Hypercare: 2 weeks post-launch

<details>
<summary><strong>Explicitly out of scope</strong> (click to expand)</summary>

- Rebranding or new visual identity — we build on the current design language
- Paid-media landing pages hosted outside acme.com
- Translation of migrated content (workflow is included, translation itself is not)

</details>

## Timeline and milestones

| Week | Milestone | Deliverable |
|---|---|---|
| 2 | IA sign-off | Sitemap, URL map, redirect plan |
| 4 | Design system ready | Tokens + 24 components in Storybook |
| 7 | Templates complete | All five templates in staging |
| 9 | Content migrated | 640 pages, automated redirect report |
| 11 | **Go-live** | Production cutover, DNS switch |
| 12–13 | Hypercare | Daily monitoring, fix-forward |

> Dates assume kickoff on the first Monday after signature. A slip in IA sign-off moves everything downstream week-for-week — it is the only hard dependency on Acme's side.

## Investment

<div class="price">

<table>
  <thead>
    <tr><th>Item</th><th>Basis</th><th>Amount (USD)</th></tr>
  </thead>
  <tbody>
    <tr><td>Discovery &amp; IA</td><td>Fixed</td><td>18,000</td></tr>
    <tr><td>Design system &amp; templates</td><td>Fixed</td><td>52,000</td></tr>
    <tr><td>Migration &amp; integrations</td><td>Fixed</td><td>34,000</td></tr>
    <tr><td>Hypercare (2 weeks)</td><td>Included</td><td>0</td></tr>
    <tr class="total"><td colspan="2">Total<sup>1</sup></td><td>104,000</td></tr>
  </tbody>
</table>

</div>

<p class="fineprint"><sup>1</sup> Excludes GST and third-party licence fees (CMS, search). Payable 40 / 40 / 20 at kickoff, template completion, and go-live.</p>

## Team

<span class="tag">Delivery lead</span> <span class="tag">Tech lead</span> <span class="tag">2 × Engineers</span> <span class="tag">Designer</span> <span class="tag">Content strategist</span>

The same team runs the project end to end — no handoffs between a "sales team" and a "delivery team". Weekly steering call every <strong>Tuesday 10:00 NZT</strong>; async updates in a shared channel. Press <kbd>Ctrl</kbd> + <kbd>K</kbd> in the staging CMS to try the editor search we set up during discovery.

## Assumptions and risks

1. Acme provides CMS and DNS access by week 1.
2. Existing imagery is licensed for reuse on the new site.
3. One consolidated feedback round per milestone (further rounds are change requests).

<div class="callout callout-warn">
⚠️ <strong>Key risk — content freeze.</strong> Pages edited in the legacy CMS after the week-8 freeze will not be migrated automatically. We flag stragglers in the migration report, but late edits need manual re-entry.
</div>

## Acceptance

Signing below accepts the scope, timeline, and investment above. This document can be **sealed** in GenPrompt — the seal proves it hasn't been modified since — and exported as a PDF twin for your records.

<div class="sig-grid">
  <div class="sig">Signature — Acme Corporation<br />Name, title, date</div>
  <div class="sig">Signature — GenPrompt Consulting<br />Name, title, date</div>
</div>

---

### Appendix: sample integration payload

The campaign template consumes product data from Acme's PIM via a nightly sync:

~~~json
{
  "sku": "ACME-1042",
  "name": "Anvil Pro 2",
  "price": { "amount": 249.0, "currency": "USD" },
  "availability": "in_stock",
  "updatedAt": "2026-07-17T02:00:00Z"
}
~~~
`,
}
