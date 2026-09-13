# АМСУР — технический план для AI-агента

## 0. Назначение документа

Этот документ является базовым техническим планом реализации АМСУР — автоматического модуля составления учебных расписаний.

Цель агента: реализовать надёжный производственный прототип, пригодный для использования школой и демонстрации на конкурсе Технопарка Беларуси.

Главный приоритет проекта:

> **Качество, корректность и воспроизводимость автоматического построения школьного расписания важнее второстепенных функций UI.**

Архитектура и конкретные алгоритмические решения не являются догмой. Агент обязан проверять их экспериментально и может заменить решение при наличии доказательств, что альтернативный подход лучше.

---

# 1. Product requirements

## 1.1. Primary outcome

Для заданной модели школы построить расписание, удовлетворяющее всем обязательным ограничениям и минимизирующее функцию качества.

Система должна поддерживать long-running optimization:

1. Find first feasible solution.
2. Accept only solution passing authoritative hard validation.
3. Continue optimization after first feasible solution.
4. Keep best-so-far.
5. Maintain Top-K archive (K=5 by default).
6. Replace archive entries when better admissible and sufficiently diverse solutions arrive.
7. Stop on cancellation/time limit/user action without losing best-so-far.

Не объявлять solution globally optimal, если solver не доказал optimality.

UI terminology:
- «лучшее найденное» — если поиск ограничен временем;
- «оптимальность доказана» — только при соответствующем solver status/proof.

## 1.2. User population

Primary user: anyone responsible for constructing a school's complete timetable.

The system must not assume technical expertise.

UX rule:
- simple defaults for typical Belarusian school;
- progressive disclosure;
- all important behavior configurable without editing source code;
- advanced parameters available but hidden from default workflow.

---

# 2. Canonical workflow

```text
School data
  -> Input validation
  -> Curriculum/load model
  -> Teacher/class/room assignments
  -> Constraint normalization
  -> Problem builder
  -> Feasibility solve
  -> Best-so-far archive
  -> Continuous optimization
  -> Top-5 diversity filtering
  -> User selection
  -> Manual editor
  -> Incremental validation/evaluation
  -> Optional limited local repair
  -> Full validation
  -> Export
```

No accepted schedule may bypass authoritative FullValidator.

---

# 3. Domain model

Minimum entities:

```text
School
AcademicYear
SchoolDay
Shift
TimeSlot
Class
Teacher
Subject
ClassSubjectAssignment
CurriculumItem
Lesson
LessonOccurrence
StudentGroup
SubgroupAssignment
Room
RoomCapability
ClassTeacherAssignment
TeacherQualification
TeacherAvailability
ClassSchedulePreference
SubjectSchedulePreference
RelationConstraint
Schedule
ScheduleVersion
ScheduleCandidate
QualityProfile
RuleDefinition
RuleWeight
PenaltyBreakdown
ValidationIssue
ReplacementOption
```

Do not introduce entities solely because they look architecturally elegant. Each entity must correspond to a real domain need or a testable invariant.

---

# 4. Core semantic rules

## 4.1. Teacher assignment

A class/subject has an assigned primary teacher.

Example:

```text
10B + Mathematics -> Teacher Ivanov
```

All weekly occurrences generated from this curriculum item use Ivanov.

No day-by-day random teacher reassignment is allowed.

Substitution is a separate feature and must not alter the base model.

## 4.2. Subject eligibility

Teacher qualification is represented operationally as a set of teachable subjects/subject families.

Optional informational qualification fields may exist but must not be the sole source of solver eligibility.

## 4.3. Rooms

Default rooms are universal unless explicit specialization exists.

Specialization model:

```text
Universal
PreferredFor(subject)
RequiredFor(subject)
ForbiddenFor(subject)
```

Room capacity and equipment are configurable.

Because the typical school is assumed to have classes that fit ordinary rooms, capacity should not dominate UX by default. It must still exist in the underlying model.

## 4.4. Subgroups

Support two subgroups by default; model must permit N groups.

A class may split differently per subject.

Example:

```text
8A + English -> subgroup A/B
8A + Informatics -> subgroup A/B
8A + DMP -> different grouping allowed
```

Synchronized subgroups share the same time variable while retaining independent teacher/room assignments.

---

# 5. Hard vs high-penalty vs soft semantics

The project must not reduce all constraints to arbitrary numeric penalties.

Use three conceptual levels:

### HARD
Cannot be violated in an accepted timetable.

Examples:
- class double-booking;
- teacher double-booking;
- forbidden room usage;
- shift incompatibility;
- required synchronization;
- impossible structural assignments;
- lesson occurrence missing from required load.

### HIGH-PENALTY SOFT
Normally avoided, but may be violated if no fully ideal solution exists.

Examples:
- student window;
- teacher window;
- preferred room unavailable;
- excessive daily load;
- non-ideal subject spacing.

### SOFT
Preference rather than correctness.

Examples:
- teacher preferences;
- preferred room rather than required room;
- subject pairing preferences;
- movement minimization.

Hardness must be represented semantically in the domain/rule catalog, not inferred solely from weight magnitude.

---

# 6. Quality model

The quality engine must provide both:

1. scalar comparable score;
2. transparent breakdown.

Recommended conceptual ordering:

```text
Feasibility
    > Student windows
    > Teacher windows
    > Sanitary/normative compliance
    > Daily load balance
    > Subject distribution
    > Room specialization/preferences
    > Teacher preferences
    > Movement/preferences
```

Implementation may use a mathematically equivalent lexicographic or dynamically weighted objective. Agent must benchmark alternatives rather than assuming a single weighted sum is always sufficient.

Critical requirement:

No lower-priority preference may make an invalid hard-conflict schedule preferable to a valid one.

---

# 7. Student windows

Default goal: zero student windows.

Student window is technically soft/high-penalty because an impossible problem must remain diagnosable.

Teacher windows receive a substantially lower default penalty than student windows.

The exact weights must be configurable.

Do not hard-code user-visible numeric constants into business logic.

---

# 8. Daily loads

Support:
- maximum lessons/day per class;
- maximum lessons/day per teacher;
- maximum consecutive lessons where useful;
- subject max occurrences/day;
- optional per-class overrides.

Default values should be supplied by a profile appropriate for a typical Belarusian school and must be explicitly versioned.

Do not present guessed normative values as legal facts.

---

# 9. Subject difficulty

Subject difficulty is a configurable score.

Support:

```text
default subject difficulty
       ↓
override for class
```

Example:

```text
Mathematics default = 5
10B Mathematics = 4
```

Difficulty should be used in quality rules such as placement in first/last periods only when such a rule is explicitly configured.

Do not claim that the score itself is an official Belarusian sanitary norm unless validated against an authoritative document.

---

# 10. Subject relations

Provide a generic rule system rather than subject-specific code.

At minimum:
- same day;
- different day;
- adjacent;
- not adjacent;
- before;
- after;
- preferred same day;
- preferred different day;
- maximum occurrences/day;
- preferred occurrences/day.

Support scope:
- school-wide subject relation;
- class-specific relation;
- possibly class+subject relation.

Priority resolution must be deterministic.

Recommended precedence:

```text
explicit class-specific rule
    > explicit subject rule
    > profile default
```

---

# 11. Teacher preferences

Support optional:
- unavailable days;
- unavailable lesson numbers;
- preferred days;
- avoided days;
- maximum lessons/day;
- maximum consecutive lessons;
- preferred first/last period boundaries;
- preferred/avoided rooms;
- optional methodical day.

Distinguish:

```text
Forbidden
Preferred
Neutral
Avoid
```

User must be able to understand each category without technical terminology.

---

# 12. Class events / special lessons

Support special fixed or preferred events such as:

```text
Class hour for all/selected classes
Thursday, lesson 1
```

Also support class teacher assignment.

Special events must be represented as scheduling entities/rules rather than hard-coded Thursday logic.

---

# 13. Shifts and timetable grid

Default:
- 5 school days;
- configurable sixth day;
- classes independently assigned to shifts;
- teacher and room resources can span shifts where physically possible.

Initial UI may use lesson numbers instead of exact bell times.

Underlying model should support optional exact time intervals without forcing the user to configure them.

---

# 14. Solver architecture

Preferred baseline remains Google OR-Tools CP-SAT because the problem is a constraint optimization/scheduling problem. Google documents CP-SAT for constraint programming and scheduling and provides a .NET API. citeturn818872search7turn818872search10

Current target platform: .NET 10 LTS. As of September 8, 2026, Microsoft lists .NET 10 as active LTS with end of support November 14, 2028. citeturn818872search0

However:

> CP-SAT is a current design choice, not an irreversible decree.

The agent may replace/augment it only after a measurable experiment demonstrates a better solution for the actual problem class.

---

# 15. CP-SAT formulation

Prefer compact integer-domain variables where possible.

Potential baseline variables per lesson occurrence:

```text
start/slot
room
teacher (only if assignment is genuinely variable)
```

Teacher should NOT be a free solver variable when a class/subject has a fixed primary teacher.

This reduces search space.

For subgroup synchronisation:

```text
shared start time
independent rooms
independent teachers
```

Use interval/no-overlap machinery where it materially improves propagation, especially for duration > 1 or resource occupancy.

Do not create enormous Boolean matrices without a benchmark proving they are better.

---

# 16. Two-stage search

Recommended baseline:

### Stage A — Feasibility

Find a hard-valid solution as quickly as possible.

No need to optimize everything before first feasible.

### Stage B — Continuous optimization

Keep improving the incumbent.

Important:
- preserve incumbent;
- expose current best score;
- capture solver status;
- use cancellation token;
- allow user-defined time/no-time limit;
- preserve result on cancellation.

---

# 17. Top-5 archive

Implement a dedicated `ScheduleCandidateArchive` rather than treating Top-5 as ordinary version history.

Each candidate stores:
- immutable snapshot/reference;
- total objective;
- full penalty breakdown;
- generation timestamp;
- solver statistics;
- rule profile version;
- diversity signature;
- optional random seed/repro metadata.

Default K = 5.

Candidate enters archive if:
1. hard-valid;
2. quality is competitive; and
3. sufficiently different from archived candidates.

---

# 18. Diversity of Top-5

Do NOT blindly store the five numerically closest solutions.

Create a timetable fingerprint/signature using assignments such as:
- class/day/slot/subject;
- optionally room and teacher where useful.

Compute a configurable or deterministic distance between candidates.

The exact distance formula may be revised by the agent after experiments.

Acceptance strategy can be:

```text
candidate better than worst
AND
candidate differs from enough existing candidates
```

or a quality-vs-diversity selection strategy.

Agent must document the selected approach and benchmark it on synthetic fixtures.

---

# 19. Result explanation

Every accepted candidate must expose:

```text
Total score
Hard violations = 0
Student windows
Teacher windows
Normative penalties
Daily-load penalties
Room penalties
Subject-distribution penalties
Teacher preference penalties
Other penalties
```

Do not expose internal OR-Tools terminology in the normal UI.

---

# 20. Manual editor

The editor must validate a proposed move before commit.

Move states:

```text
VALID
VALID_WITH_PENALTY
FORBIDDEN
```

Preview must provide:
- resulting score delta;
- affected entities;
- exact blocking causes if forbidden.

The editor must not invoke a complete CP-SAT generation for ordinary cell validation.

Use incremental/local validation.

After commit:
- recalculate affected quality components;
- update total score;
- update issue state;
- record ChangeSet;
- support Undo/Redo.

---

# 21. Optional local repair

Add an explicit user command:

> Repair this change automatically.

Scope must be limited to a small neighborhood around the requested change.

Examples:
- move selected lesson;
- swap two lessons;
- relocate 2–N affected lessons.

Do not silently rebuild the entire school's schedule because of one manual edit.

The result must show:
- number of changed lessons;
- old score;
- new score;
- hard validity;
- explanation.

---

# 22. Replacement teachers

Treat substitutions as secondary.

Model:

```text
PrimaryTeacher
ReplacementTeacher[]
```

Replacement ranking should prefer:
1. same subject qualification;
2. compatible subject family;
3. allowed availability;
4. minimal disruption.

Do not change the base timetable automatically during normal generation just because a substitute exists.

---

# 23. Impossibility / diagnostics

Distinguish at least these statuses:

```text
FEASIBLE
INFEASIBLE_CONFIRMED_BY_MODEL
NO_SOLUTION_FOUND_WITHIN_LIMIT
CANCELLED_AFTER_FEASIBLE
CANCELLED_WITHOUT_FEASIBLE
```

Never show all timeout cases as mathematical impossibility.

Diagnostics should combine:
- input validation;
- solver status;
- bottleneck analysis;
- constraint grouping;
- optional assumption-core/conflict analysis;
- targeted relaxation probes.

Relaxation is diagnostic only. A relaxed result can never be accepted as a valid timetable if it violates a HARD rule.

---

# 24. Normative rules / Belarusian data

Do not hard-code legal assumptions as timeless truth.

Create a versioned ruleset:

```text
NormativeRuleSet
  Version
  EffectiveFrom
  Sources[]
  Rules[]
```

Every legal/normative rule needs:
- source;
- version/date;
- human description;
- machine expression;
- severity;
- enabled state.

The current plan must be revalidated against the authoritative Belarusian normative documents immediately before release.

The existing source project overview explicitly flags the legal verification of SanPiN defaults as an open release item, so the agent must not silently mark this as completed. fileciteturn0file0L486-L493

Similarly, the source project already identifies that some current performance conclusions are not fully proven for medium/large synthetic instances; these must remain measured facts, not marketing assumptions. fileciteturn0file0L441-L450

---

# 25. Built-in Belarusian subjects

Provide a built-in reference-data package for typical Belarusian schools.

However:
- reference data must be versioned;
- exact class-to-subject mapping must be sourced from current official curricula/plans;
- user can add/remove/override subjects;
- missing subject must never block an advanced user from creating a custom subject.

Do not assume that one static subject list covers all school types, profiles, electives and future years.

---

# 26. UI architecture

Primary flow:

```text
Home
School Data
  - Classes
  - Subjects
  - Teachers
  - Rooms
  - Load
  - Subgroups
  - Time/Shifts
Requirements / Profile
Schedule
  - Generate
  - Top-5
  - Diagnostics
  - Versions
Settings / Backup
```

Use progressive disclosure.

Default user must not need to understand:
- CP-SAT;
- Hard/Soft terminology;
- objective;
- solver seed;
- workers;
- mathematical weights.

Advanced view may expose them.

---

# 27. School data wizard

Recommended order:

1. School/year.
2. Classes and shifts.
3. Subjects.
4. Teachers + teachable subjects.
5. Rooms + specializations.
6. Curriculum/load + primary teacher assignments.
7. Subgroups.
8. Optional preferences.
9. Validation/readiness.
10. Generate.

Subjects must precede teachers so a teacher can be associated with a newly added or existing subject.

---

# 28. Default profiles

Implement as data, not hard-coded branches.

Suggested defaults:

```text
STANDARD
STUDENT_FRIENDLY
TEACHER_FRIENDLY
NORMATIVE
CUSTOM
```

Profiles modify rule weights/settings but do not rewrite core constraints.

---

# 29. Persistence

SQLite is suitable as local source of truth for the single-PC V1.

Store:
- school configuration;
- academic year;
- generated schedules;
- Top-5 candidates;
- rulesets;
- profile versions;
- edit history;
- backups.

Use atomic persistence around schedule acceptance.

Never overwrite active schedule until the new candidate has passed FullValidator.

---

# 30. Export

Mandatory:
- Excel;
- PDF.

Views:
- classes;
- teachers;
- rooms.

Before export:
1. FullValidator.
2. Ensure no unapproved hard conflicts.
3. Generate report.

---

# 31. Testing strategy

Testing must focus primarily on solver correctness, not on raw line coverage.

Fixtures:

```text
TinyFeasible
TinyInfeasible
TeacherConflict
ClassConflict
RoomConflict
RoomSpecialization
StudentWindow
TeacherWindow
Subgroups2
Subgroups3
DifferentSubgroupSchemes
Shifts
SubjectDistribution
TeacherAvailability
ReplacementTeacher
Top5Diversity
LongRunningOptimization
CancelAfterFeasible
CancelBeforeFeasible
ManualMoveValid
ManualMoveForbidden
ManualMoveSoftPenalty
LocalRepair
```

Every regression fixture should have deterministic input and, where solver nondeterminism affects the assertion, a controlled seed or robust invariant-based assertion.

---

# 32. Key acceptance criteria

## Core solver

- Every accepted schedule has zero authoritative hard violations.
- Required weekly lesson load is fully represented.
- No teacher/class/forbidden-room collisions.
- Subgroup synchronization is correct.
- Shift restrictions are correct.

## Optimization

- First feasible solution is retained.
- Later better solution replaces best-so-far.
- Top-5 updates during long-running search.
- Cancellation preserves best-so-far.
- Candidate scores have a reproducible breakdown.

## UX

- Typical user can create a timetable without editing configuration files.
- Forbidden manual moves explain why they are forbidden.
- Valid moves show resulting quality impact.
- Hard conflicts cannot be silently saved.

## Diagnostics

- Timeout is not mislabeled as infeasible.
- True model infeasibility yields useful causes when possible.

---

# 33. Performance philosophy

Do not promise:
> «Any school is solved in seconds.»

Performance is instance-dependent.

The correct product behavior is:

```text
find feasible
→ improve continuously
→ show current best
→ let user stop
```

Benchmark using realistic synthetic schools and, when permitted, anonymized pilot data.

The existing project report shows a known bottleneck in finding the first feasible schedule on dense synthetic instances; this must be measured rather than hidden. fileciteturn0file0L431-L450

---

# 34. Development strategy

Priority order:

### P0 — Solver correctness
Domain → constraints → first feasible → FullValidator → objective → long-running improvement.

### P1 — Top-5 and explanations
Candidate archive → diversity → breakdown → live progress.

### P2 — Data and usability
Wizard → CRUD → profiles → advanced settings.

### P3 — Manual editing
Incremental evaluator → drag/drop → undo/redo → local repair.

### P4 — Reliability and output
Persistence → backup → Excel/PDF → diagnostics → installer.

Secondary features must never delay core solver correctness.

---

# 35. Agent autonomy rule

The agent is authorized and expected to modify the technical plan when evidence supports a better solution.

Before changing a major architectural decision, the agent must:

1. state the original decision;
2. describe the observed problem;
3. propose the alternative;
4. compare both;
5. run a focused experiment/benchmark when practical;
6. record the decision and evidence;
7. update affected documentation/tests.

Examples of legitimate changes:
- alternate CP-SAT model;
- different objective formulation;
- different Top-5 diversity metric;
- different persistence abstraction;
- different UI architecture if WPF limitations materially hurt the required experience;
- different diagnostic algorithm.

The agent must NOT change core semantics merely to make implementation easier.

---

# 36. Do not over-engineer

Do not implement complex enterprise features merely because the domain could theoretically need them.

V1 should prioritize:

```text
complete school timetable
+ quality optimization
+ Top-5
+ transparent quality
+ manual corrections
+ safe save/export
```

Defer unless a real use case appears:
- cloud collaboration;
- multi-user editing;
- web client;
- mobile app;
- enterprise identity;
- complex replacement workflow;
- excessive what-if tooling.

---

# 37. Current baseline from existing project

The attached existing project overview reports a strong architectural baseline: WPF/MVVM, SQLite, CP-SAT, FullValidator, incremental evaluation, subgroup support, generation/editing/versions and extensive tests are already represented. fileciteturn0file0L161-L180

The agent should therefore **refactor and correct where necessary rather than blindly rebuild everything**.

The same source explicitly identifies release gaps including legal verification of normative defaults, Excel split round-trip, large-grid rendering measurement, distribution/installer and a solver-flake test. fileciteturn0file0L486-L494

These are existing engineering findings and must be incorporated into the backlog rather than forgotten.

---

# 38. Final definition of done

The project is ready for competition/pilot when:

1. A clean database can be populated through the normal UI.
2. A typical school can be modeled without code changes.
3. A valid timetable is generated.
4. The solver can continue improving it after first feasible.
5. Top-5 updates correctly.
6. The user can stop and retain the best solution.
7. All accepted schedules pass FullValidator.
8. Quality breakdown is understandable.
9. Manual edits are validated safely.
10. Impossible/timeout states are distinguished honestly.
11. Data can be exported to usable school formats.
12. Legal/normative defaults have been checked against current authoritative sources.
13. The application has been tested on at least one realistic school dataset.

---

# 39. Principle above all others

If an implementation decision conflicts with the project's main goal, choose the solution that produces a **more correct, more useful and more consistently high-quality timetable**, even if the implementation is technically less elegant.

The system exists to build a good school timetable. Everything else is secondary.
