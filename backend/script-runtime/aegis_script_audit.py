"""Return an inspection-only view of the program, without user-controlled literals or names."""

import io
import keyword
import token
import tokenize


def shape(source):
    pieces = []
    strings = 0
    try:
        for part in tokenize.generate_tokens(io.StringIO(source).readline):
            if part.type in (token.FSTRING_START, token.TSTRING_START):
                if not strings:
                    pieces.append("<texto>")
                strings += 1
            elif part.type in (token.FSTRING_END, token.TSTRING_END):
                strings -= 1
            elif strings:
                continue
            elif part.type == token.NAME:
                pieces.append(part.string if keyword.iskeyword(part.string) else "<nome>")
            elif part.type == token.OP:
                pieces.append(part.string)
            elif part.type == token.STRING:
                pieces.append("<texto>")
            elif part.type == token.NUMBER:
                pieces.append("<número>")
            elif part.type in (token.NEWLINE, token.NL):
                pieces.append("\n")
    except (SyntaxError, tokenize.TokenError):
        return "<estrutura indisponível>"
    return " ".join(pieces)[:2048]
