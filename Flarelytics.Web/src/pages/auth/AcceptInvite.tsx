import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearchParams } from "react-router";
import { errorMessage, request } from "../../api/client";
import { keys } from "../../api/hooks";
import type { InvitationPreview } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { AuthLayout } from "../../components/AuthLayout";
import { Alert, Button, PageLoader } from "../../components/ui";

const roleLabel = { Owner: "owner", Admin: "admin", Viewer: "lettore" } as const;

/**
 * Dove porta il link dell'invito. Tre casi: l'utente è già dentro (accetta con
 * un clic), ha un account ma non è dentro (prima accede), non ha un account
 * (si registra dall'invito).
 */
export function AcceptInvitePage() {
  const [params] = useSearchParams();
  const token = params.get("token") ?? "";
  const { status } = useAuth();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const preview = useQuery({
    queryKey: ["invitation", token],
    queryFn: () => request<InvitationPreview>(`/invitations/preview?token=${encodeURIComponent(token)}`, { anonymous: true }),
    retry: false,
  });

  const accept = useMutation({
    mutationFn: () => request<{ organizationId: string }>("/invitations/accept", { method: "POST", body: { token } }),
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: keys.me });
      navigate(`/o/${r.organizationId}`, { replace: true });
    },
  });

  if (preview.isPending || status === "loading") return <PageLoader />;

  if (preview.error) {
    return (
      <AuthLayout title="Invito non valido">
        <Alert tone="bad">{errorMessage(preview.error)}</Alert>
      </AuthLayout>
    );
  }

  const invitation = preview.data;
  const back = `/accept-invite?token=${encodeURIComponent(token)}`;

  return (
    <AuthLayout
      title={`Entra in ${invitation.organizationName}`}
      subtitle={`${invitation.invitedBy} ti ha invitato come ${roleLabel[invitation.role]}. L'invito è per ${invitation.email}.`}
    >
      <div className="space-y-4">
        {status === "authenticated" ? (
          <>
            <Button variant="primary" className="h-9 w-full" loading={accept.isPending} onClick={() => accept.mutate()}>
              Accetta l'invito
            </Button>
            {accept.error && <Alert tone="bad">{errorMessage(accept.error)}</Alert>}
          </>
        ) : invitation.accountExists ? (
          <Link to={`/login?next=${encodeURIComponent(back)}`}>
            <Button variant="primary" className="h-9 w-full">Accedi per accettare</Button>
          </Link>
        ) : (
          <Link to={`/register?invite=${encodeURIComponent(token)}`}>
            <Button variant="primary" className="h-9 w-full">Crea l'account</Button>
          </Link>
        )}
      </div>
    </AuthLayout>
  );
}
