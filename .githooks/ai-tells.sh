#!/bin/sh
# Shared by pre-commit + commit-msg: patterns that are obvious signs of
# AI-generated text or of mangled (mojibake) encoding.
#
# Three kinds of rule, all handed to LC_ALL=C grep -E:
#   raw bytes    written as \0nnn octal and expanded with printf '%b'. Octal,
#                not \xNN: \xNN is a bashism that dash silently leaves as the
#                literal text "\xe2", and a rule that expands to nonsense never
#                matches anything - the hook would pass everything on a Linux
#                or macOS /bin/sh. %b (not the format form) so a pattern may
#                contain a % of its own.
#   ASCII spelling of a character that is not ASCII: &mdash;, &#8212;, a
#                backslash-u escape, %E2%80%94, a pair of \xNN escapes. A
#                byte-level check cannot see any of these - they are 7-bit
#                ASCII in the source and still render as the exact character
#                they exist to block. Caught in the wild after an uninstall
#                dialog shipped three HTML entities.
#   plain ERE    the AI credit trailers.
#
# The soft class ends in a catch-all for ANY byte >= 0x80, so a character
# nobody thought to list (bullet, star, section sign, a Cyrillic homoglyph)
# still blocks; the named byte rules above it exist only to put a useful word
# in the error message. One character is exempt, U+00A9: the Windows and macOS
# copyright strings in forge.config.js / package.json need it. Its ASCII
# spellings (&copy;, a backslash-u escape) stay blocked - where the character
# itself is allowed there is no reason to spell it out.
#
# Escape hatch (per commit):  ALLOW_UNICODE=1 git commit ...
#   relaxes the SOFT class below (typography / emoji / their ASCII spellings)
#   for that one commit. The HARD class (mojibake, U+FFFD, BOM, zero-width and
#   bidi controls, AI credit lines) is never intentional and is enforced
#   regardless. --no-verify is not allowed.

# label|pattern  (one per line; the pattern may contain | for alternation)
SOFT_TELLS='em-dash|\0342\0200\0224
en-dash|\0342\0200\0223
horizontal-bar|\0342\0200\0225
curly-quote|\0342\0200[\0230-\0237]
ellipsis-char|\0342\0200\0246
bullet|\0342\0200[\0242\0243]
prime|\0342\0200[\0262\0263\0264]
non-breaking-space|\0302\0240
soft-hyphen|\0302\0255
invisible-space|\0342\0200[\0200-\0212\0257]|\0343\0200\0200
emoji|\0360\0237
math-alphanumeric|\0360\0235
symbol-or-checkmark|\0342[\0230-\0237]
box-drawing-or-arrow|\0342[\0206\0207\0224-\0227\0254\0255]
unicode-escape|\\u(00[89a-fA-F][0-9a-fA-F]|0[1-9a-fA-F][0-9a-fA-F]{2}|[1-9a-fA-F][0-9a-fA-F]{3})|\\U0000(00[89a-fA-F][0-9a-fA-F]|0[1-9a-fA-F][0-9a-fA-F]{2}|[1-9a-fA-F][0-9a-fA-F]{3})|\\U000[1-9a-fA-F][0-9a-fA-F]{4}
braced-escape|\\[uUxX]\{0*([89a-fA-F][0-9a-fA-F]|[0-9a-fA-F]{3,})\}
named-escape|\\N\{[A-Z][A-Z ]+\}
css-escape|content:[^;]*\\[0-9a-fA-F]{4}
hex-byte-escape|\\[xX][89a-fA-F][0-9a-fA-F]\\[xX][0-9a-fA-F]{2}
entity-no-semicolon|&(nbsp|copy|reg|deg|middot|laquo|raquo|plusmn|times|divide|frac1[24]|frac34|sup[123]|micro|para|sect|cent|pound|yen|curren|iexcl|iquest|acute|uml|cedil|macr|ordf|ordm|not|shy|brvbar|mdash|ndash|horbar|hellip|mldr|[lr][sd]quo|bull|dagger|trade|check|star)([^;A-Za-z0-9]|$)
numeric-entity-no-semicolon|&#(12[89]|1[3-9][0-9]|[2-9][0-9][0-9]|[0-9]{4,})([^;0-9]|$)|&#[xX]0*([89a-fA-F][0-9a-fA-F]|[0-9a-fA-F]{3,})([^;0-9a-fA-F]|$)
percent-encoded-utf8|%[89a-fA-F][0-9a-fA-F]%[0-9a-fA-F]{2}'

HARD_TELLS='zero-width-char|\0342\0200[\0213-\0217]
word-joiner|\0342\0201\0240
bidi-control|\0342\0200[\0252-\0256]|\0342\0201[\0246-\0251]
byte-order-mark|\0357\0273\0277
replacement-char-U+FFFD|\0357\0277\0275
mojibake-utf8-as-latin1|\0303\0242\0342\0202\0254
mojibake-A-tilde|\0303\0203[\0302\0303\0251\0250\0244\0266\0274\0247]
mojibake-replacement|\0303\0257\0302\0277\0302\0275
ai-credit-trailer|(Co-?[Aa]uthored|[Aa]ssisted|[Gg]enerated|[Cc]reated|[Ww]ritten)-?[ -]?[Bb]y:.*([Cc]laude|[Aa]nthropic|[Cc]opilot|ChatGPT|OpenAI|Gemini|Codex|Cursor|Devin|Aider)
ai-generated-with|[Gg]enerated (with|by).*([Cc]laude|[Aa]nthropic|[Cc]opilot|ChatGPT|OpenAI|Gemini|Codex|Cursor)
ai-session-trailer|^[[:space:]]*(Claude|Codex|Cursor|Copilot|Gemini)-Session:
ai-tool-url|claude\.ai/code|claude\.com/claude-code|anthropic\.com/claude-code|noreply@anthropic\.com'

if [ "${ALLOW_UNICODE:-0}" = "1" ]; then
  AI_TELLS="$HARD_TELLS"
  AI_TELLS_SOFT=0
  AI_TELLS_MODE="ALLOW_UNICODE=1: typography/emoji relaxed for this commit; mojibake and AI credit still enforced"
else
  AI_TELLS="$SOFT_TELLS
$HARD_TELLS"
  AI_TELLS_SOFT=1
  AI_TELLS_MODE=""
fi

# HTML entities get their own pass: everything except the five that genuinely
# have to be escaped in HTML/XML is a tell, so there is no denylist of entity
# names to keep up to date. Numeric ones are decoded and compared, which covers
# &#8212;, &#x2014; and any zero-padded spelling of either. A name needs two or
# more characters and at least one lowercase letter, so that shell redirects
# (2>&1;) and C-style bitwise code (flags&MASK;) are not read as entities; the
# cost is that the all-caps spellings HTML also accepts (&COPY; &REG;) are not
# seen. Written with spaces, as every formatter here wants it, a bitwise & is
# not an entity at all.
scan_entities() {
  LC_ALL=C awk '
    function bad(e,   h, v, i, c) {
      sub(/^&/, "", e); sub(/;$/, "", e)
      if (e ~ /^#[0-9]+$/) return (substr(e, 2) + 0) >= 128
      if (e ~ /^#[xX][0-9a-fA-F]+$/) {
        h = tolower(substr(e, 3)); v = 0
        for (i = 1; i <= length(h); i++) {
          c = index("0123456789abcdef", substr(h, i, 1)) - 1
          v = v * 16 + c
        }
        return v >= 128
      }
      if (e ~ /^#/) return 0
      if (length(e) < 2) return 0
      if (e ~ /^[A-Z0-9]+$/) return 0
      return !(tolower(e) in SAFE)
    }
    BEGIN { split("amp lt gt quot apos", s, " "); for (i in s) SAFE[s[i]] = 1 }
    {
      t = $0
      while (match(t, /&(#[xX]?[0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]*);/)) {
        e = substr(t, RSTART, RLENGTH); t = substr(t, RSTART + RLENGTH)
        if (bad(e)) { print; break }
      }
    }' "$1"
}

# The catch-all: any byte >= 0x80 left over, minus the one exempt character.
scan_high_bytes() {
  LC_ALL=C awk -v ok="$(printf '%b' '\0302\0251')" -v hi="$(printf '%b' '[\0200-\0377]')" '
    { t = $0; gsub(ok, "", t); if (t ~ hi) print }' "$1"
}

# scan_ai_tells <file-with-lines>: prints "  [label] <line>" for each hit. A
# line is reported once, under the most specific rule that names it.
scan_ai_tells() {
  _hits=$(mktemp) || return 1
  printf '%s\n' "$AI_TELLS" | while IFS='|' read -r label pat; do
    [ -z "$pat" ] && continue
    # Only the byte rules go through printf: a pattern with no \0 in it is
    # already an ERE, and %b would eat the backslashes it needs.
    case $pat in *'\0'*) pat=$(printf '%b' "$pat") ;; esac
    LC_ALL=C grep -aE -- "$pat" "$1" 2>/dev/null | sed "s/^/  [$label] /"
  done > "$_hits"
  if [ "$AI_TELLS_SOFT" = 1 ]; then
    _seen=$(mktemp) || return 1
    sed 's/^  \[[^]]*\] //' "$_hits" > "$_seen"
    scan_entities "$1"   | LC_ALL=C grep -aFxv -f "$_seen" | sed 's/^/  [html-entity] /'    >> "$_hits"
    sed 's/^  \[[^]]*\] //' "$_hits" > "$_seen"
    scan_high_bytes "$1" | LC_ALL=C grep -aFxv -f "$_seen" | sed 's/^/  [non-ascii-byte] /' >> "$_hits"
    rm -f "$_seen"
  fi
  cut -c1-160 "$_hits"
  rm -f "$_hits"
}
