# PD-ADR-002: Sell in the US only, with a single flat sales-tax rate

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CHK-R06, CHK-R08, [00-overview](../00-overview.md) §7 |

## Context
The market decides currency, tax, payment providers and carriers. The first choice was "single country, generic". The decision to use a real carrier API changed that: carrier aggregators with good free test modes (for example EasyPost, Shippo) expect US addresses. US sales tax varies by state, county and city, and sometimes by product type.

## Decision drivers
- A real carrier API must work in test mode.
- Keep the tax model from taking effort away from the core problems.
- Be honest about simplifications.

## Considered options
**Market**
1. Vietnam only
2. US only
3. Single country, generic
4. Multi-country

**Tax (once the US was chosen)**
1. External tax API (for example Stripe Tax, TaxJar, Avalara)
2. Per-state rate table
3. Single flat rate

## Decision
Chosen: **US only (USD, US addresses, US carriers) with one flat sales-tax rate set in configuration, applied to goods only**. The US makes a real carrier API usable in test mode. A flat rate keeps tax out of the critical path.

## Pros and cons of the options
### US only
- ✅ Carrier and payment sandboxes work as-is.
- ❌ Realistic US tax is complex.

### Single country, generic
- ✅ Clean, country-neutral domain.
- ❌ Doesn't fit a real carrier API, which is country-specific.

### Vietnam only / Multi-country
- ❌ Vietnam: weak fit with the chosen carrier sandboxes. Multi-country: large scope.

### External tax API
- ✅ Realistic; how real US stores work.
- ❌ Another external dependency and failure mode inside checkout.

### Per-state table
- ✅ In-house, somewhat realistic.
- ❌ Still wrong for city and county rates. Staff upkeep.

### Single flat rate
- ✅ Simplest, no dependency.
- ❌ Unrealistic for the US.

## Consequences
- ✅ Tax calculation is trivial and can't fail at checkout.
- ❌ Tax amounts aren't legally accurate. Must be documented as a deliberate simplification (A-03).
- ⚠️ Moving to real tax later means a tax-calculation step at checkout start and tax stored per line (already per line via paid amount).

## Links
- [05-checkout-payment](../05-checkout-payment.md)
