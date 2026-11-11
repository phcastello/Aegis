// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Email;

public sealed class EmailProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
