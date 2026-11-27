namespace Aegis.Domain.Entities;

public sealed class OwnerPreferences
{
    public const int SingletonId = 1;
    public const int MaximumInstructionsLength = 2000;

    public int Id { get; private set; } = SingletonId;
    public string CustomInstructions { get; private set; } = "";
    public Guid Revision { get; private set; }

    public void Update(string instructions)
    {
        if (instructions.Length > MaximumInstructionsLength || instructions.Contains('\0'))
            throw new ArgumentException("Informe instruções válidas de até 2.000 caracteres.");
        CustomInstructions = instructions.Trim();
        Revision = Guid.NewGuid();
    }
}
