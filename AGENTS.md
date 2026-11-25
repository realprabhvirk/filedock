# AGENTS.md

## publik API
- Base URL: https://publikhq.com/api/v1 (OpenAI Chat Completions, Responses, Anthropic Messages)
- Models: publik-fast, publik-balanced, publik-smart or a vendor/model name from GET /models; never a bare model name
- Sign in with publik: the publik module's gate runs at start-up, before the first screen; it also sends usage telemetry
- Key: per-install `pk_` from POST /installs, stored at the publik credential path; env PUBLIK_API_KEY / PUBLIK_API_BASE_URL override
- App token: publik-app-token.txt (public by design, committed) or PUBLIK_APP_TOKEN
- 402: show error.message + error.top_up_url only
- Show use: GET /wallet used_percent or prepaid_remaining_percent, never money
- Never print or log a key
- Docs: https://publikhq.com/llms.txt, https://publikhq.com/developers, https://publikhq.com/skills/api.md

FileDock specifics: the module is `src/FileDock/Services/Publik.cs` (`APP_SLUG = "filedock"`, `USES_AI = false`).
`App.OnStartup` runs `Publik.EnsureSignedInAsync()` before the shelf window; `PUBLIK_SELF_TEST=1` prints the self-test line and exits.
