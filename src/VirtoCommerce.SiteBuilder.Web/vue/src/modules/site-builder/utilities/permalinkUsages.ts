import {
  ApiException,
  SiteBuilderPermalinkConflict,
  type SiteBuilderPermalinkUsage,
} from "../../../api_client/virtocommerce.sitebuilder";

// Publishing returns 409 when another page got published at the permalink for the same visitors after the check
// before publishing. Returns the pages it lists, or null for any other error.
export function getConflictsOfRejectedPublish(error: unknown): SiteBuilderPermalinkUsage[] | null {
  if (!(error instanceof ApiException) || error.status !== 409) {
    return null;
  }

  try {
    const body = JSON.parse(error.response) as { conflicts?: SiteBuilderPermalinkUsage[] };
    return body.conflicts ?? [];
  } catch {
    return [];
  }
}

export interface PublishConflicts {
  // Published pages this page replaces for all their visitors and dates: publishing is refused while they stay published.
  blocking: SiteBuilderPermalinkUsage[];
  // Published pages this page meets for some of their visitors or dates only; elsewhere they are still shown.
  possible: SiteBuilderPermalinkUsage[];
}

export function getPublishConflicts(usages: SiteBuilderPermalinkUsage[] | null | undefined): PublishConflicts {
  const list = usages ?? [];
  return {
    blocking: list.filter((x) => x.conflict === SiteBuilderPermalinkConflict.Certain),
    possible: list.filter((x) => x.conflict === SiteBuilderPermalinkConflict.Possible),
  };
}

// A page with unpublished changes cannot be unpublished, so the conflict has to be resolved on that page first.
export function canUnpublishAll(usages: SiteBuilderPermalinkUsage[]): boolean {
  return usages.every((x) => x.canUnpublish !== false);
}

// Pages in the same language, or in any language, are the ones the storefront can mix up.
export function getUsagesInLanguage(
  usages: SiteBuilderPermalinkUsage[] | null | undefined,
  cultureName: string | null | undefined,
): SiteBuilderPermalinkUsage[] {
  return (usages ?? []).filter(
    (x) => !x.cultureName || !cultureName || x.cultureName.toLowerCase() === cultureName.toLowerCase(),
  );
}

export function formatPageNames(usages: SiteBuilderPermalinkUsage[]): string {
  return usages.map((x) => `"${x.name || x.groupId}"`).join(", ");
}
