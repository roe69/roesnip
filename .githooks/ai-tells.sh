#!/bin/sh
# Shared by pre-commit + commit-msg: byte-level patterns that are obvious signs
# of AI-generated text or of mangled (mojibake) encoding. Matched with
# LC_ALL=C grep -E so each entry is a raw UTF-8 byte sequence.
# Keep in sync with CLAUDE.md hard rule 10 (plugin code is 7-bit ASCII).
#
# Escape hatch (per commit):  ALLOW_UNICODE=1 git commit ...
#   relaxes the SOFT class below (typography / emoji) for that one commit.
#   The HARD class (mojibake, U+FFFD, BOM, zero-width, AI credit lines) is
#   never intentional and is enforced regardless. --no-verify is not allowed.

# label|bytes  (one per line)
SOFT_TELLS="em-dash|\xe2\x80\x94
en-dash|\xe2\x80\x93
horizontal-bar|\xe2\x80\x95
curly-quote|\xe2\x80[\x98\x99\x9c\x9d]
ellipsis-char|\xe2\x80\xa6
non-breaking-space|\xc2\xa0
emoji|\xf0\x9f
symbol-or-checkmark|\xe2[\x9c\x9d\x9e\x9a\x98\x97\x96]
box-drawing-or-arrow|\xe2[\x94\x95\x96\x86\x87\x8c]"

HARD_TELLS="zero-width-char|\xe2\x80[\x8b\x8c\x8d\x8f]
word-joiner|\xe2\x81\xa0
byte-order-mark|\xef\xbb\xbf
replacement-char-U+FFFD|\xef\xbf\xbd
mojibake-utf8-as-latin1|\xc3\xa2\xe2\x82\xac
mojibake-A-tilde|\xc3\x83[\xc2\xc3\xa9\xa8\xa4\xb6\xbc\xa7]
mojibake-replacement|\xc3\xaf\xc2\xbf\xc2\xbd
ai-credit-trailer|[Cc]o-[Aa]uthored-[Bb]y:.*([Cc]laude|[Aa]nthropic|[Cc]opilot|ChatGPT|OpenAI|Gemini|Codex)
ai-generated-with|[Gg]enerated (with|by).*([Cc]laude|[Aa]nthropic|[Cc]opilot|ChatGPT|OpenAI|Gemini|Codex)
anthropic-noreply|noreply@anthropic\.com"

if [ "${ALLOW_UNICODE:-0}" = "1" ]; then
  AI_TELLS="$HARD_TELLS"
  AI_TELLS_MODE="ALLOW_UNICODE=1: typography/emoji relaxed for this commit; mojibake and AI credit still enforced"
else
  AI_TELLS="$SOFT_TELLS
$HARD_TELLS"
  AI_TELLS_MODE=""
fi

# scan_ai_tells <file-with-lines>: prints "  [label] n:<line>" for each hit.
scan_ai_tells() {
  printf '%s\n' "$AI_TELLS" | while IFS='|' read -r label pat; do
    [ -z "$pat" ] && continue
    # shellcheck disable=SC2059
    bpat=$(printf "$pat")
    LC_ALL=C grep -E -- "$bpat" "$1" 2>/dev/null | cut -c1-160 | sed "s/^/  [$label] /"
  done
}
