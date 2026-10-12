# User Preference Audit

`User_Preference_*` and `User_Preferred_*` columns in the three master audit CSVs are authoritative product-policy inputs. Preserve the user's preferred fix type and rationale across implementation, docs, and regenerated audit files. Do not silently downgrade or remove a preference to match current implementation status.

## Current preference records

- Historical `PRJ077` preference is preserved on canonical `PRJ014` and the historical row. `Verified` is the desired target state; execution remains blocked until retained-reference/escape-lifetime evidence, a concrete action and a deterministic verifier are available.
- Historical `PRJ078` preference is preserved on canonical `PRJ015` and the historical row. `Verified` is the desired default-disable policy; execution remains blocked until sufficiently complete sensor-consumer absence evidence, a concrete action and a deterministic verifier are available.
- Historical `PRJ081` resolves to canonical `PRJ063`; its synchronization-dependency caveat remains part of the canonical rule policy.

## Compatibility and audit structure

Alias mappings and `SHD011` compatibility behavior are listed once in the [Rule Audit README](README.md). The retained historical rows support lookup and preference provenance; they do not represent separate active findings.

Group-level overlap conditions and verification plans are maintained only in `ProjectOptimizer_Rule_Overlap_Matrix.csv`; master CSV overlap cells refer to that matrix to avoid repeating large blocks of text. The package owner confirmed Unity compilation for the current package. Test Runner and real-asset validation remain unconfirmed.
