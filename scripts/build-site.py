# Builds the GitHub Pages site in ten languages from one template:
#   docs/_site/page.html + docs/_site/i18n/<lang>.json  →  docs/index.html (English) and docs/<path>/index.html
# Every page has its own address (so search engines index each language), links to the others (hreflang), plays the film
# in its own language and the visitor's light/dark theme, and the English page sends a first-time visitor to their language.
#   python scripts/build-site.py
import json, os, re, html

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, 'docs')
SITE = 'https://ds3owl.github.io/islebar/'
# code (films, i18n file), url path, html lang / hreflang, og locale, name in its own language
LANGS = [
    ('en', '', 'en', 'en_US', 'English'),
    ('ko', 'ko/', 'ko', 'ko_KR', '한국어'),
    ('ja', 'ja/', 'ja', 'ja_JP', '日本語'),
    ('zh', 'zh/', 'zh-Hans', 'zh_CN', '简体中文'),
    ('zht', 'zh-tw/', 'zh-Hant', 'zh_TW', '繁體中文'),
    ('fr', 'fr/', 'fr', 'fr_FR', 'Français'),
    ('de', 'de/', 'de', 'de_DE', 'Deutsch'),
    ('it', 'it/', 'it', 'it_IT', 'Italiano'),
    ('pt', 'pt/', 'pt-BR', 'pt_BR', 'Português'),
    ('es', 'es/', 'es', 'es_ES', 'Español'),
]


def load(code):
    with open(os.path.join(DOCS, '_site', 'i18n', code + '.json'), encoding='utf-8') as f:
        return json.load(f)


def media(code, name, theme):
    # a clip filmed in this page's language when there is one, otherwise the English one
    own = os.path.join(DOCS, 'media', code, f'{name}-{theme}.webp')
    return f'media/{code}/{name}-{theme}.webp' if code != 'en' and os.path.exists(own) else f'media/{name}-{theme}.webp'


def picture(code, root, name, w, h, alt):
    return (f'<picture><source media="(prefers-color-scheme: dark)" srcset="{root}{media(code, name, "dark")}">'
            f'<img src="{root}{media(code, name, "light")}" alt="{html.escape(alt)}" loading="lazy" width="{w}" height="{h}"></picture>')


def build(code, path, lang, locale, own_name, en):
    t = {**en, **load(code)}
    root = '../' * path.count('/')
    feats = '\n'.join(
        f'      <div class="feat"><div class="media">{picture(code, root, f["media"], 800, 450, f["alt"])}</div>'
        f'<div class="text"><h3>{f["title"]}</h3>{f["body"]}</div></div>' for f in t['features'])
    faq = '\n'.join(f'    <h3>{q}</h3><p>{a}</p>' for q, a in t['faq'])
    hreflang = '\n'.join(f'<link rel="alternate" hreflang="{l}" href="{SITE}{p}">' for _, p, l, _, _ in LANGS)
    hreflang += f'\n<link rel="alternate" hreflang="x-default" href="{SITE}">'
    links = ' · '.join(
        (f'<b>{n}</b>' if c == code else f'<a href="{root}{p}" data-lang="{c}" hreflang="{l}">{n}</a>') for c, p, l, _, n in LANGS)
    ld_app = json.dumps({
        '@context': 'https://schema.org', '@type': 'SoftwareApplication', 'name': 'IsleBar', 'operatingSystem': 'Windows 11',
        'applicationCategory': 'UtilitiesApplication', 'inLanguage': lang, 'description': t['ld_description'],
        'offers': {'@type': 'Offer', 'price': '0', 'priceCurrency': 'USD'}, 'license': 'https://opensource.org/license/mit',
        'downloadUrl': 'https://github.com/ds3owl/islebar/releases/latest', 'image': SITE + 'media/og.png', 'url': SITE + path,
    }, ensure_ascii=False, indent=2)
    strip = lambda s: re.sub(r'<[^>]+>', '', s)
    ld_faq = json.dumps({
        '@context': 'https://schema.org', '@type': 'FAQPage', 'inLanguage': lang,
        'mainEntity': [{'@type': 'Question', 'name': strip(q), 'acceptedAnswer': {'@type': 'Answer', 'text': strip(a)}} for q, a in t['faq']],
    }, ensure_ascii=False, indent=2)
    redirect = ''
    if code == 'en':
        # first visit only: send the visitor to their language (a choice made in the footer is remembered)
        table = {c: p for c, p, *_ in LANGS if c != 'en'}
        redirect = ('<script>(function(){try{var s=localStorage.getItem("islebar-lang");if(s)return;}catch(e){}'
                    'var map=' + json.dumps(table) + ';'
                    'var list=navigator.languages&&navigator.languages.length?navigator.languages:[navigator.language||"en"];'
                    'for(var i=0;i<list.length;i++){var l=(list[i]||"").toLowerCase();'
                    'if(l.indexOf("zh")===0){location.replace(/tw|hk|mo|hant/.test(l)?map.zht:map.zh);return;}'
                    'var b=l.split("-")[0];if(b==="en")return;if(map[b]){location.replace(map[b]);return;}}})();</script>')
    with open(os.path.join(DOCS, '_site', 'page.html'), encoding='utf-8') as f:
        page = f.read()
    with open(os.path.join(DOCS, '_site', 'style.css.html'), encoding='utf-8') as f:
        style = f.read()
    page = re.sub(r'\{\{pic:(\w+):(\d+):(\d+):(\w+)\}\}', lambda m: picture(code, root, m[1], m[2], m[3], t[m[4]]), page)
    values = {**t, 'html_lang': lang, 'og_locale': locale, 'canonical': SITE + path, 'hreflang': hreflang, 'film': code,
              'root': root, 'features': feats, 'faq_html': faq, 'lang_links': links, 'ld_app': ld_app, 'ld_faq': ld_faq,
              'redirect': redirect, 'style': style}
    page = re.sub(r'\{\{(\w+)\}\}', lambda m: str(values[m[1]]), page)
    page = page.replace('{{root}}', root)
    out = os.path.join(DOCS, path, 'index.html')
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(page)
    return out


if __name__ == '__main__':
    en = load('en')
    for code, path, lang, locale, name in LANGS:
        print(build(code, path, lang, locale, name, en))
    # sitemap with every language, so search engines find them all
    with open(os.path.join(DOCS, 'sitemap.xml'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">\n')
        for _, p, _, _, _ in LANGS:
            f.write(f'  <url><loc>{SITE}{p}</loc>\n')
            for _, p2, l2, _, _ in LANGS:
                f.write(f'    <xhtml:link rel="alternate" hreflang="{l2}" href="{SITE}{p2}"/>\n')
            f.write('  </url>\n')
        f.write('</urlset>\n')
    with open(os.path.join(DOCS, 'robots.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(f'User-agent: *\nAllow: /\nSitemap: {SITE}sitemap.xml\n')
