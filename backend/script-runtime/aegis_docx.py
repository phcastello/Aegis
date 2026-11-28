"""Read text and write simple Word documents using WordprocessingML packages."""
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile

from defusedxml import ElementTree as SafeXml


_WORD = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
_XML = "http://www.w3.org/XML/1998/namespace"
_RELATIONSHIPS = "http://schemas.openxmlformats.org/package/2006/relationships"
_OFFICE_DOCUMENT = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"
_CONTENT_TYPES = "http://schemas.openxmlformats.org/package/2006/content-types"
_MAX_DOCUMENT_XML = 16 * 1024 * 1024


def read_paragraphs(source):
    """Return body and table paragraphs, in document order, from a path or a binary stream."""
    with ZipFile(source) as package:
        part = package.getinfo("word/document.xml")
        if part.file_size > _MAX_DOCUMENT_XML:
            raise ValueError("The Word document body exceeds 16 MiB")
        with package.open(part) as xml:
            root = SafeXml.parse(xml).getroot()
    return ["".join(_inline_text(node) for node in paragraph.iter())
            for paragraph in root.iter(f"{{{_WORD}}}p")]


def _inline_text(node):
    if node.tag == f"{{{_WORD}}}t":
        return node.text or ""
    if node.tag == f"{{{_WORD}}}tab":
        return "\t"
    if node.tag == f"{{{_WORD}}}br":
        return "\n"
    return ""


def _paragraph(parent, text):
    paragraph = ElementTree.SubElement(parent, f"{{{_WORD}}}p")
    run = ElementTree.SubElement(paragraph, f"{{{_WORD}}}r")
    for index, line in enumerate(str(text).split("\n")):
        if index:
            ElementTree.SubElement(run, f"{{{_WORD}}}br")
        ElementTree.SubElement(run, f"{{{_WORD}}}t", {f"{{{_XML}}}space": "preserve"}).text = line


def write_document(destination, paragraphs, *, tables=()):
    """Create a DOCX containing paragraphs and optional tables made of rows and cells."""
    document = ElementTree.Element(f"{{{_WORD}}}document")
    body = ElementTree.SubElement(document, f"{{{_WORD}}}body")
    for paragraph in paragraphs:
        _paragraph(body, paragraph)
    for rows in tables:
        table = ElementTree.SubElement(body, f"{{{_WORD}}}tbl")
        rows = [list(row) for row in rows]
        table_properties = ElementTree.SubElement(table, f"{{{_WORD}}}tblPr")
        ElementTree.SubElement(table_properties, f"{{{_WORD}}}tblW",
                               {f"{{{_WORD}}}w": "0", f"{{{_WORD}}}type": "auto"})
        grid = ElementTree.SubElement(table, f"{{{_WORD}}}tblGrid")
        for _ in range(max(map(len, rows), default=0)):
            ElementTree.SubElement(grid, f"{{{_WORD}}}gridCol")
        for cells in rows:
            row = ElementTree.SubElement(table, f"{{{_WORD}}}tr")
            for cell in cells:
                _paragraph(ElementTree.SubElement(row, f"{{{_WORD}}}tc"), cell)

    content = (f'<Types xmlns="{_CONTENT_TYPES}">'
               '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
               '<Default Extension="xml" ContentType="application/xml"/>'
               '<Override PartName="/word/document.xml" '
               'ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
               '</Types>')
    relationships = (f'<Relationships xmlns="{_RELATIONSHIPS}">'
                     f'<Relationship Id="rId1" Type="{_OFFICE_DOCUMENT}" Target="word/document.xml"/>'
                     '</Relationships>')
    with ZipFile(destination, "w", ZIP_DEFLATED) as package:
        package.writestr("[Content_Types].xml", content)
        package.writestr("_rels/.rels", relationships)
        package.writestr("word/document.xml", ElementTree.tostring(document, encoding="utf-8", xml_declaration=True))
