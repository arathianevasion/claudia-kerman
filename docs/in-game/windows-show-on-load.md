# The chat and Missions windows show (#976)

1. Open KSP with Claudia Kerman installed and load a save.
2. At the Space Center and in Flight, open the Claudia chat and the Missions windows from the toolbar buttons. Expect both to show.
3. Search KSP.log for `Loader.Awake`: expect no exception (no NullReferenceException) from it.
4. In Flight, quickload or revert: expect no exception in the log, and a `find_landing_site` answer made later than the resumed UT is forgotten by the next `deorbit` (#974).
