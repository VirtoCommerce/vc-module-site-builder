import type { GroupedSiteBuilderPage } from "../../api_client/virtocommerce.sitebuilder";

export type PageLifecycle = "Draft" | "Pending" | "Active" | "Archived";

/** Mirrors the server's page search filters: a published page waiting for its start date is pending. */
export function pageLifecycle(
  page: Pick<GroupedSiteBuilderPage, "status" | "startDate" | "endDate">,
  now = new Date(),
): PageLifecycle {
  if (page.status === "Archived" || (page.endDate && new Date(page.endDate) < now)) {
    return "Archived";
  }
  if (page.status !== "Published") {
    return "Draft";
  }
  return page.startDate && new Date(page.startDate) > now ? "Pending" : "Active";
}
