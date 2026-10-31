# LLD-ADR-008: Verify the email before the account exists, using a Data Protection registration token

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | ACC-R01, ACC-R02, LCH-R06, [06-identity.md §6.0](../06-identity.md) |

## Context
The first draft created the customer account first and verified the email afterwards. That allows unverified accounts registered with someone else's email, which enables account pre-hijacking (an attacker registers your email, then waits for you to link Google) and forces every service to check an `email_verified` flag.

## Decision drivers
- No account for an email whose owner hasn't proven control of it.
- The email used to create the account can't be swapped by the client.
- Fewer "unverified" states across services.

## Considered options
1. Verify first: email → code → protected registration token → account (owner's proposal)
2. Create first, verify later (first draft)

**Existing email at sign-up:** say "already registered" · identical response + "you already have an account" email
**Token:** 30 min stateless · 15 min one-time (Redis)
**Key ring:** Postgres encrypted at rest · Redis

## Decision
Chosen: **verify first**. `StartSignUp(email)` sends a 6-digit code (or answers `EMAIL_ALREADY_REGISTERED`); `VerifySignUpCode` returns a **Data Protection time-limited token** (purpose `bloom.registration.v1`, 30 min, payload email); `CompleteSignUp(token, password, name)` creates the account using **only the token's email**. The key ring is stored in identity's Postgres, encrypted with a certificate from Sealed Secrets.

## Pros and cons of the options
### Verify first
- ✅ No unverified customer accounts; pre-hijacking impossible; Google linking becomes safe to automate.
- ✅ `email_verified` flag, `ev` claim and launch's verified-email check disappear.
- ❌ One more step before the password form.

### Create first
- ❌ Junk and pre-hijacked accounts possible; every service checks a flag.

### Saying "already registered"
- ✅ Clear for real users.
- ❌ Sign-up reveals which emails have accounts; mitigated by per-email and per-IP rate limits.

## Consequences
- ✅ ACC-R01 and ACC-R02 simplified (product rules updated).
- ❌ Account enumeration through sign-up is accepted (ID-05 rewritten).
- ⚠️ Codes travel to notification encrypted (AES-256-GCM, key shared only by identity and notification).

## Links
- [06-identity.md §4.3, §6.0, §6.2, §6.5](../06-identity.md)
