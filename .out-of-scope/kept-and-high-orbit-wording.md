# Rewording "kept" and "high orbit" in Claudia's text

The High Route (#866) keeps a high ellipse, and the text Claudia sees says "kept" and "high orbit" for it. The glossary's _Avoid_ line lists kept orbit, high orbit and kept ellipse, and #902 asked whether the text should follow it, or the result key be renamed.

## Why this is out of scope

"Kept" and "high orbit" are plain English, and Claudia reads them correctly. Rewording them changes the cached prefix and the goldens and changes no behaviour.

Renaming the result key risks plans that use it.

The glossary term stays for code and briefs. The _Avoid_ line governs those, not the text Claudia sees.

## Prior requests

- #902: "Reword the remaining 'kept orbit' / 'high orbit' text Claudia sees to High Route (from #882)"
