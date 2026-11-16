import json, re, sys, datetime
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.enums import TA_CENTER
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph, Spacer, Image,
                                Table, TableStyle, KeepTogether, PageBreak, NextPageTemplate,
                                ListFlowable, ListItem, Flowable, CondPageBreak)
from reportlab.platypus.tableofcontents import TableOfContents
from PIL import Image as PILImage

data, assets, out, version = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
for w in ['Regular', 'SemiBold', 'Bold', 'Italic']:
    pdfmetrics.registerFont(TTFont(f'Inter-{w}', f'Inter-{w}.ttf'))
pdfmetrics.registerFont(TTFont('DejaVu', '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'))
pdfmetrics.registerFont(TTFont('Mono', '/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf'))
from reportlab.lib.fonts import addMapping
from reportlab.pdfbase.pdfmetrics import registerFontFamily
registerFontFamily('Inter-Regular', normal='Inter-Regular', bold='Inter-Bold', italic='Inter-Italic', boldItalic='Inter-Bold')

INK = colors.HexColor('#1B1F27'); MUTED = colors.HexColor('#5B6472'); RULE = colors.HexColor('#D9DEE6')
BLUE = colors.HexColor('#1F6FD1'); BLUE_BG = colors.HexColor('#EAF2FC')
AMBER = colors.HexColor('#B7791F'); AMBER_BG = colors.HexColor('#FDF4E3'); CHIP = colors.HexColor('#EEF0F3')

base = dict(fontName='Inter-Regular', fontSize=10, leading=14.5, textColor=INK)
S = {
    'body': ParagraphStyle('body', **base, spaceAfter=6),
    'li': ParagraphStyle('li', **base),
    'sub': ParagraphStyle('sub', fontName='Inter-SemiBold', fontSize=11.5, leading=15, textColor=INK, spaceBefore=8, spaceAfter=4),
    'h1': ParagraphStyle('h1', fontName='Inter-Bold', fontSize=20, leading=24, textColor=INK, spaceAfter=2),
    'summary': ParagraphStyle('summary', fontName='Inter-Regular', fontSize=11, leading=15, textColor=MUTED, spaceAfter=10),
    'num': ParagraphStyle('num', fontName='Inter-SemiBold', fontSize=9, leading=12, textColor=BLUE),
    'cap': ParagraphStyle('cap', fontName='Inter-Italic', fontSize=8.5, leading=11, textColor=MUTED, alignment=TA_CENTER, spaceBefore=3),
    'box': ParagraphStyle('box', **{**base, 'fontSize': 9.5, 'leading': 13.5}),
    'cell': ParagraphStyle('cell', **{**base, 'fontSize': 9.5, 'leading': 13}),
}

EMOJI = re.compile('[\U0001F300-\U0001FAFF☀-➿️]')
FALLBACK = set('↶↷⟲⟳')

def md(text):
    text = EMOJI.sub(lambda m: '' if m.group() not in '✕' else m.group(), text)
    text = text.replace('()', '').replace('( ', '(')
    text = re.sub(r'\s{2,}', ' ', text).strip()
    text = text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
    text = re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', text)
    text = re.sub(r'`(.+?)`', lambda m: f'<font name="Mono" size="8.8" backColor="#EEF0F3">&nbsp;{m.group(1)}&nbsp;</font>', text)
    text = ''.join(f'<font name="DejaVu">{c}</font>' if c in FALLBACK or c == '✕' else c for c in text)
    return text

W = A4[0] - 40 * mm

def picture(file, caption):
    path = f'{assets}/{file}'
    w, h = PILImage.open(path).size
    width = min(W, w * 0.62)
    height = width * h / w
    if height > 120 * mm:
        height = 120 * mm; width = height * w / h
    img = Image(path, width=width, height=height)
    frame = Table([[img]], colWidths=[width + 2], style=[('BOX', (0, 0), (-1, -1), 0.6, RULE),
                                                         ('LEFTPADDING', (0, 0), (-1, -1), 1), ('RIGHTPADDING', (0, 0), (-1, -1), 1),
                                                         ('TOPPADDING', (0, 0), (-1, -1), 1), ('BOTTOMPADDING', (0, 0), (-1, -1), 1)])
    return KeepTogether([Spacer(1, 4), frame, Paragraph(md(caption), S['cap']), Spacer(1, 8)])

def callout(label, text, fg, bg):
    p = Paragraph(f'<font name="Inter-SemiBold" color="{fg.hexval().replace("0x", "#")}">{label}</font>&nbsp;&nbsp;{md(text)}', S['box'])
    t = Table([[p]], colWidths=[W], style=[('BACKGROUND', (0, 0), (-1, -1), bg), ('LINEBEFORE', (0, 0), (0, -1), 3, fg),
                                          ('LEFTPADDING', (0, 0), (-1, -1), 10), ('RIGHTPADDING', (0, 0), (-1, -1), 10),
                                          ('TOPPADDING', (0, 0), (-1, -1), 7), ('BOTTOMPADDING', (0, 0), (-1, -1), 7)])
    return KeepTogether([Spacer(1, 3), t, Spacer(1, 8)])

def items(lst, numbered):
    flow = ListFlowable([ListItem(Paragraph(md(x), S['li']), spaceAfter=3) for x in lst],
                        bulletType='1' if numbered else 'bullet', start='1' if numbered else '•',
                        bulletFontName='Inter-SemiBold' if numbered else 'Inter-Regular', bulletFontSize=9.5,
                        bulletColor=BLUE, leftIndent=16, bulletDedent=12)
    return [flow, Spacer(1, 6)]

def keytable(rows):
    data = [[Paragraph('<b>Tecla</b>', S['cell']), Paragraph('<b>Ação</b>', S['cell'])]]
    data += [[Paragraph(f'<font name="Mono" size="9">{md(k)}</font>', S['cell']), Paragraph(md(a), S['cell'])] for k, a in rows]
    t = Table(data, colWidths=[55 * mm, W - 55 * mm], repeatRows=1)
    t.setStyle(TableStyle([('BACKGROUND', (0, 0), (-1, 0), CHIP), ('LINEBELOW', (0, 0), (-1, -1), 0.5, RULE),
                           ('VALIGN', (0, 0), (-1, -1), 'MIDDLE'), ('TOPPADDING', (0, 0), (-1, -1), 5),
                           ('BOTTOMPADDING', (0, 0), (-1, -1), 5)]))
    return [t, Spacer(1, 10)]

class SectionHeading(Flowable):
    """Título de seção que também alimenta o sumário e os marcadores do PDF."""
    def __init__(self, n, title):
        super().__init__(); self.n, self.title = n, title
        self.p = Paragraph(md(title), S['h1'])
    def wrap(self, aw, ah):
        w, h = self.p.wrap(aw, ah); self.h = h; return aw, h
    def draw(self):
        self.p.drawOn(self.canv, 0, 0)
        key = f'sec{self.n}'
        self.canv.bookmarkPage(key); self.canv.addOutlineEntry(f'{self.n}. {self.title}', key, 0)

class Doc(BaseDocTemplate):
    def afterFlowable(self, f):
        if isinstance(f, SectionHeading):
            self.notify('TOCEntry', (0, f'{f.n:02d}&nbsp;&nbsp;&nbsp;{md(f.title)}', self.page, f'sec{f.n}'))

def cover(c, doc):
    c.saveState()
    c.setFillColor(colors.HexColor('#12151B')); c.rect(0, 0, A4[0], A4[1], fill=1, stroke=0)
    lw = 130 * mm; lh = lw * 136 / 437
    c.drawImage(f'{assets}/../BrandLogo.png', (A4[0] - lw) / 2, A4[1] * 0.58, lw, lh, mask='auto')
    c.setFillColor(colors.white); c.setFont('Inter-Bold', 26)
    c.drawCentredString(A4[0] / 2, A4[1] * 0.47, 'Manual do operador')
    c.setFillColor(colors.HexColor('#9AA4B2')); c.setFont('Inter-Regular', 11.5)
    c.drawCentredString(A4[0] / 2, A4[1] * 0.47 - 22, 'Telões de estúdio: grade, cenas, saídas, editor de imagem e controle remoto')
    c.setFont('Inter-Regular', 9.5)
    c.drawCentredString(A4[0] / 2, 22 * mm, f'Versão {version}  ·  {datetime.date.today().strftime("%d/%m/%Y")}')
    c.restoreState()

def page(c, doc):
    c.saveState()
    c.setStrokeColor(RULE); c.setLineWidth(0.5); c.line(20 * mm, 15 * mm, A4[0] - 20 * mm, 15 * mm)
    c.setFont('Inter-Regular', 8); c.setFillColor(MUTED)
    c.drawString(20 * mm, 10 * mm, 'RWVideoWall · Manual do operador')
    c.drawRightString(A4[0] - 20 * mm, 10 * mm, str(doc.page))
    c.restoreState()

doc = Doc(out, pagesize=A4, title='RWVideoWall — Manual do operador', author='RWVideoWall',
          leftMargin=20 * mm, rightMargin=20 * mm, topMargin=18 * mm, bottomMargin=22 * mm)
frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id='f')
doc.addPageTemplates([PageTemplate('cover', [frame], onPage=cover), PageTemplate('body', [frame], onPage=page)])

story = [NextPageTemplate('body'), PageBreak()]
toc = TableOfContents()
toc.levelStyles = [ParagraphStyle('toc', fontName='Inter-Regular', fontSize=11, leading=20, textColor=INK)]
toc.dotsMinLevel = 0
story += [Paragraph('Sumário', S['h1']), Spacer(1, 10), toc, PageBreak()]

secs = json.load(open(data, encoding='utf-8'))
for n, sec in enumerate(secs, 1):
    title, summary, *blocks = sec['args']
    if n > 1: story += [Spacer(1, 14), CondPageBreak(90 * mm)]
    story += [Paragraph(f'SEÇÃO {n:02d}', S['num']), SectionHeading(n, title), Paragraph(md(summary), S['summary'])]
    for b in blocks:
        t, a = b['type'], b['args']
        if t == 'Paragraph': story.append(Paragraph(md(a[0]), S['body']))
        elif t == 'Subtitle': story.append(Paragraph(md(a[0]), S['sub']))
        elif t == 'Bullets': story += items(a, False)
        elif t == 'Steps': story += items(a, True)
        elif t == 'Picture': story.append(picture(a[0], a[1]))
        elif t == 'Tip': story.append(callout('Dica', a[0], BLUE, BLUE_BG))
        elif t == 'Warning': story.append(callout('Atenção', a[0], AMBER, AMBER_BG))
        elif t == 'KeyTable': story += keytable(a)
        else: raise Exception(t)

doc.multiBuild(story)
print('ok', doc.page)
