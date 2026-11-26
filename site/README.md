# emendly.dev — static site

Zero build step. Open `index.html` directly, or serve the folder:

```sh
python3 -m http.server 8080 --directory site
```

Files:

| File | Purpose |
|---|---|
| `index.html` | Landing page: hero, showcase, actions, support, privacy summary, FAQ |
| `privacy.html` | Plain-English privacy policy |
| `styles.css` | All styling, one file, no framework |
| `script.js` | Screenshot tabs, copy button, donation button, footer year |
| `assets/` | Screenshots and app icon copied from `docs/media/` and the Windows shell |
| `CNAME.example` | Custom-domain template; copy to `CNAME` when the domain is live |

## Enabling the donation link

The **Buy me a coffee** button stays hidden until a URL exists. Edit one line in `script.js`:

```js
const DONATE_URL = "https://www.buymeacoffee.com/…";
```

Leave it empty and the button never renders; the GitHub Sponsors button is always shown.

## GitHub Pages

`.github/workflows/pages.yml` deploys the `site/` folder on every push to `main` that touches it.
One-time setup in the repository:

1. **Settings → Pages → Build and deployment → Source**: choose **GitHub Actions**.
2. Push to `main`, or run the **Pages** workflow manually from the **Actions** tab.

The site is then published at `https://pwwang.github.io/emendly/`. All asset paths are relative,
so it works unchanged under that subpath.

## Custom domain (`emendly.dev`)

1. Copy `CNAME.example` to `CNAME` in this folder (`site/CNAME`) and commit it. GitHub Pages reads
   that file on each deploy.
2. At your DNS provider, point the apex domain at GitHub:

   | Type | Name | Value |
   |---|---|---|
   | `A` | `@` | `185.199.108.153` |
   | `A` | `@` | `185.199.109.153` |
   | `A` | `@` | `185.199.110.153` |
   | `A` | `@` | `185.199.111.153` |
   | `CNAME` | `www` | `pwwang.github.io` |

   (Those four `A` records are GitHub Pages' documented apex addresses.)
3. In **Settings → Pages → Custom domain**, enter `emendly.dev` and enable **Enforce HTTPS** once
   the certificate is issued.

## Keeping screenshots current

The images in `assets/` are copies of the real app screenshots. To refresh them:

```sh
cp docs/media/menu.png docs/media/streaming.png docs/media/result.png docs/media/settings.png site/assets/
cp shell/windows/Emendly.App/Assets/emendly-256.png site/assets/
```

If the pixel dimensions change, update the `width`/`height` values in `SHOTS` inside `script.js`
and the hero `<img>` in `index.html` so the layout does not shift while images load.
