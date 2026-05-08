#!/bin/bash
# UserPromptSubmit hook for the Unity / Quest 3 project.
#
# Detects screenshot-related phrasing in the user's prompt and injects a
# strict system reminder forcing the next response to call Read on the
# canonical screencap PNG. This makes "show me a screencap" deterministic
# rather than relying on the model to recall and apply a memory rule.
#
# Trigger: any prompt matching `screen.?(shot|cap)` or `\b(show|display)\b`
# Action: emit JSON with hookSpecificOutput.additionalContext.

set -u

# Read JSON from stdin; pull the user prompt.
INPUT=$(cat)
PROMPT=$(printf '%s' "$INPUT" | jq -r '.prompt // ""' 2>/dev/null)

# If we can't parse the prompt, do nothing (don't break the user's turn).
[ -z "$PROMPT" ] && exit 0

# Trigger regex: case-insensitive, broad enough to catch the common phrasings
# ("show a screenshot", "display it", "screencap please", "show me what's on
# the device"). False positives (e.g., "show me how to ...") are harmless —
# worst case the model gets an extra reminder it ignores.
if echo "$PROMPT" | grep -qiE 'screen.?(shot|cap)|\b(show|display)\b'; then
    cat <<'EOF'
{
  "hookSpecificOutput": {
    "hookEventName": "UserPromptSubmit",
    "additionalContext": "SCREENSHOT RULE (HARD, hook-injected): the user message references a screenshot or display action. Your response MUST include a `Read` tool call on `/tmp/quest_latest_shot.png`. If they're asking for a NEW capture (\"take\", \"show a\", \"new\", \"fresh\"), prefix with one `Bash` call: `/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh screencap`. NEVER substitute a paragraph about CCD rendering, output_image, or Preview. NEVER ask if they want you to display it. NEVER describe the image as a substitute for displaying it. The Read goes in the same response, full stop. If the user is pushing back (\"you didn't display it\", \"show it again\", \"why are you not following\"), the answer is one Read on the PNG and nothing else."
  }
}
EOF
fi
exit 0
