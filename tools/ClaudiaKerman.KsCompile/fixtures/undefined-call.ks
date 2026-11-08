// Fixture for the undefined-calls gate (#928): line 4 calls a function nothing defines, as four block tests once
// called deorbit_ksc after #790 removed it. The delegate and built-in calls around it must not be reported.
local plan is lexicon("a", 1).
deorbit_ksc(plan).
local d is circularize@.
d().
