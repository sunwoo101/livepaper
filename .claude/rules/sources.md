---
paths:
  - "src/livepaper/Scrapers/**"
  - "src/livepaper/Services/**"
---

## Wallpaper Sources

All HTTP requests must send a Firefox User-Agent:
```text
Mozilla/5.0 (X11; Linux x86_64; rv:150.0) Gecko/20100101 Firefox/150.0
```
Use a single shared `HttpClient` instance (not one per request).

### motionbgs.com (HtmlAgilityPack)

**Listing:** `GET https://www.motionbgs.com/hx2/latest/{page}/`
- Parse `//a` tags: thumbnail from `.//img[src]` (prefer `data-cfsrc` over `src` for Cloudflare lazy-load), title from `.//span[@class='ttl']`, resolution from `.//span[@class='frm']`, page URL from `a[href]`
- Skip links where path is empty, starts with `tag:`, or starts with `search`

**Search:** `GET https://www.motionbgs.com/search?q={query}&page={page}`
- May redirect to tag page (e.g. `/tag:car/`) — detect via final URL
- Tag pages: use `ParseLinks` (same as listing)
- Search results: parse `//div[contains(@class,'tmb')]` → `//a` tags
- Thumbnail: try `img[data-cfsrc]` → `img[src]` → `noscript > img[src]` (use explicit `if (string.IsNullOrEmpty)` checks, not `??` chains — `GetAttributeValue` returns `""` not null)

**Individual page** (fetched before download):
- Preview video: `//source[@type='video/mp4'][src]`
- Download link: `//div[@class='download']//a[href]`

### moewalls.com (HtmlAgilityPack)

Plain HTTP with Firefox User-Agent works — no browser automation needed.

**Listing:** `GET https://moewalls.com/page/{page}`
**Search:** `GET https://moewalls.com/page/{page}/?s={query}`
- Parse `//li[contains(@class,'g1-collection-item')]`
- Thumbnail: `.//img[src]`
- Title + page URL: `.//a[@class='g1-frame'][title, href]`

**Individual page:**
- Preview video: `//source[@type='video/mp4'][src]` — prefix with base URL if relative
- Download element: `//*[@id='moe-download']` (use `*` not `button` — element type changed)
- Download URL: `https://go.moewalls.com/download.php?video={data-url}`
- **Downloads require a `Referer` header** set to the wallpaper's page URL.

### Wallpaper Engine local

- Workshop path: `~/.local/share/Steam/steamapps/workshop/content/431960/`
- Discovery from each `<id>/project.json`:
  - Skip when `type` isn't `"video"` (filters out `scene`, `web`, `application`)
  - `file` field gives the actual video filename
  - `title` field becomes the wallpaper title
- Thumbnail: `preview.jpg` preferred; GIF thumbnails → static frame via ffmpeg cached to `~/.cache/livepaper/we_thumbs/<workshopId>.jpg`; fallback to any `.png`/`.jpg`/`.jpeg`
- `SupportsSorting` returns true only for this source; sort UI visible only when WE local is selected
