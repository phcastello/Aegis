// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Email;

public sealed class EmailNotConnectedException : InvalidOperationException
{
    public EmailNotConnectedException()
        : base("Gmail is not connected.")
    {
    }
}
