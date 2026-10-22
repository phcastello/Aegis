using Aegis.Domain.Entities;
namespace Aegis.Infrastructure.Proactivity;

public static class ReminderReliabilityPolicy
{
    // Existing domain signals have a five-minute TTL. The last retry at five
    // minutes gets its complete delivery window before escalation at ten minutes.
    public static readonly TimeSpan PendingSyncLimit = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan[] RecoveryOffsets = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5)];
    public static readonly TimeSpan[] ModelRetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
    public static bool HasProblem(GeographicReminderRule rule, DateTimeOffset now) => rule.Status == "active" &&
        (rule.EndsAt is null || rule.EndsAt > now) &&
        (rule.SyncStatus is "pending_sync" or "recoverable_error" || LocalLeaseExpired(rule, now));
    public static bool LocalLeaseExpired(GeographicReminderRule rule, DateTimeOffset now) => rule.LeaseUntil <= now || rule.SyncedAt is { } synced && synced.AddDays(7) <= now;
    public static DateTimeOffset StartedAt(GeographicReminderRule rule) => rule.FailureSince ?? rule.SyncStartedAt;
    public static (string Diagnosis, string Action) Diagnose(GeographicReminderRule rule, bool observerUnreachable = false) => rule.ErrorCode switch {
        "background_location_required" => ("O Android informou que falta permissão de localização em segundo plano.", "Habilitar localização em segundo plano para a Aegis no celular."),
        "location_permission_required" => ("O Android informou que falta permissão de localização.", "Permitir localização para a Aegis no celular."),
        "precise_location_required" => ("O Android informou que falta localização precisa.", "Habilitar localização precisa para a Aegis no celular."),
        "location_services_disabled" => ("O Android informou que os serviços de localização estão desligados.", "Ativar a localização no celular e abrir a Aegis."),
        "location_provider_unavailable" => ("O Android informou que o provedor de localização está indisponível.", "Abrir a Aegis no celular e verificar os serviços de localização."),
        "region_limit_exceeded" => ("O Android informou que o limite de regiões geográficas foi excedido.", "Abrir Lembretes/Lugares e reduzir as áreas ou pausar regras dispensáveis."),
        "geometry_required" => ("O Android informou que falta uma área válida para a regra.", "Revisar a área do lugar em Lugares."),
        _ when observerUnreachable => ("As solicitações de sincronização não encontraram um canal disponível para o celular observador. A ativação não foi confirmada. A causa exata não foi identificada.", "Abrir a Aegis no celular observador para restabelecer a conexão e concluir a ativação."),
        _ => ("A ativação continua sem confirmação após as solicitações de sincronização registradas. A causa exata não foi identificada.", "Abrir a Aegis no celular observador e verificar este lembrete; se continuar bloqueado, informar o estado exibido.")
    };
}
