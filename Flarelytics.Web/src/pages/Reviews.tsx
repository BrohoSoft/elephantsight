import { ReviewList } from "../components/ReviewList";
import { PageHeader } from "../components/ui";

export function ReviewsPage() {
  return (
    <>
      <PageHeader title="Recensioni" description="App Store e Google Play insieme, dalla più recente. Rispondi da qui: la risposta arriva sullo store." />
      <ReviewList />
    </>
  );
}
