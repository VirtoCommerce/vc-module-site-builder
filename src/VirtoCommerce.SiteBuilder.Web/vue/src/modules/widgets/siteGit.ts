import { ApiException, type GitContentSiteStatus } from "../../api_client/virtocommerce.sitebuilder";

export type SiteGitView = "setup" | "error" | "off" | "missing" | "on";

export function siteGitView(status: GitContentSiteStatus): SiteGitView {
  if (!status.configured) return "setup";
  if (status.error) return "error";
  if (!status.enabled) return "off";
  return status.url ? "on" : "missing";
}

/**
 * A store keeps its pages in git while GitHub is not usable, so its pages are refused until GitHub is connected or
 * git is turned off for it. Git switched off for the whole platform is reported as off.
 */
export const gitHubMissing = (status: GitContentSiteStatus) => !!status.enabled && !status.configured;

/** The Git endpoints say what went wrong in `{ error }`, except a refusal, which has no body. */
export function gitErrorMessage(error: unknown, forbidden: string): string {
  if (!(error instanceof ApiException)) return error instanceof Error ? error.message : String(error);
  return reasonOf(error.response) ?? (error.status === 403 ? forbidden : `${error.status} ${error.message}`);
}

function reasonOf(body: string): string | undefined {
  try {
    return (JSON.parse(body) as { error?: string }).error;
  } catch {
    return undefined;
  }
}
