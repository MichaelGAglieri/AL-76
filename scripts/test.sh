#!/bin/zsh
# Test di AL-76 (dalla 1.30.83): i comandi senza modello e la chiamata, su frasi inventate e, su questo
# Mac, sulle frasi vere di voce.log e diagnostica.log confrontate con local/istantanea_comandi.tsv.
#   scripts/test.sh                      tutti i test
#   AL76_ACCETTA=1 scripts/test.sh       accetta le frasi capite in modo diverso (dopo averle guardate)
cd "${0:A:h}/.."
LOG=$(mktemp -t al76-test)
swift test --parallel > "$LOG" 2>&1
STATUS=$?
grep -E '✔|✘|↳ [0-9]+ frasi|Frasi vere|frasi capite|prima .* → ora|Se va bene|error:|Test run' "$LOG"
[[ $STATUS -ne 0 ]] && echo "✗ Test non passati (registro completo: $LOG)" || rm -f "$LOG"
exit $STATUS
