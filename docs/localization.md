# UI localization

THub's management UI supports exactly two locales:

- `en` — English and the source-language fallback;
- `zh-TW` — Traditional Chinese as used in Taiwan.

`zh-CN` resources and Mainland China terminology are outside the supported product
contract. Taiwan terminology such as `資料`, `連線`, `伺服器`, `使用者`, `設定`,
`執行`, and `發佈` is required.

## Culture selection

`THub.Web` uses ASP.NET Core request localization. The `.AspNetCore.Culture` cookie has
first priority and persists an explicit selection for one year. Without that cookie,
the first request uses the browser's `Accept-Language` header: any Chinese browser
locale selects `zh-TW`, and all other values select English. The language selector in
the main navigation writes the cookie and reloads the current local route so server
culture, document `lang`, date/number formatting, and rendered text change together.

Only `en` and `zh-TW` are accepted by the culture endpoint. Unknown values fall back to
English, and redirects are restricted to local paths.

## Resource structure

Shared UI resources live under:

```text
src/THub.Web/Resources/Localization/SharedResource.resx
src/THub.Web/Resources/Localization/SharedResource.zh-TW.resx
```

English UI text is the resource key and neutral value. Razor components use the shared
`IStringLocalizer<SharedResource>` exposed by `_Imports.razor`. The Taiwan-Chinese
resource is the reviewed translation contract.

`wwwroot/locales/zh-TW.json` mirrors the shared resource for messages emitted by
existing component code and third-party controls after the initial render. New
first-party Razor markup should use `Localizer` directly rather than relying only on
this compatibility layer.

## Adding or changing UI

Every UI change must be complete in both supported locales in the same change:

1. Write concise English source text and use `Localizer` for headings, labels, buttons,
   help text, empty states, validation, notifications, titles, placeholders, and
   accessibility attributes.
2. Add the same key to the neutral and `zh-TW` shared resources and keep the JSON mirror
   synchronized when the text can be emitted after render.
3. Use placeholders for values; do not assemble a sentence from independently
   translated fragments. Keep identifiers, user-entered values, database metadata,
   paths, and secrets outside localization.
4. Review Taiwan terminology and punctuation. Do not add `zh-CN`, Simplified Chinese,
   or Mainland China wording.
5. With explicit validation authorization, check both locales at desktop and mobile
   widths, including focus order, accessible names, overflow, dates/numbers, dialogs,
   notifications, empty/error states, and cookie persistence.

Missing translations must not be treated as complete UI work. English fallback is a
resilience mechanism, not an acceptable substitute for a `zh-TW` entry.
