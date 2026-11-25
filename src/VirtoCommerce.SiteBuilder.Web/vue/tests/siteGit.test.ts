import assert from "node:assert/strict";
import test from "node:test";
import { gitHubMissing, siteGitView } from "../src/modules/widgets/siteGit";

test("the Git panel shows setup, an error, the switch, a repository to make, or the live connection", () => {
  assert.equal(siteGitView({ configured: false, enabled: false }), "setup");
  assert.equal(siteGitView({ configured: true, enabled: true, error: "App not installed" }), "error");
  assert.equal(siteGitView({ configured: true, enabled: false, repository: "acme/site-s1" }), "off");
  assert.equal(siteGitView({ configured: true, enabled: true, repository: "acme/site-s1" }), "missing");
  assert.equal(siteGitView({ configured: true, enabled: true, repository: "acme/site-s1", url: "https://github.com/acme/site-s1" }), "on");
});

test("a store is warned about GitHub only while it keeps its pages in git and GitHub is not usable", () => {
  assert.equal(gitHubMissing({ configured: false, enabled: true }), true);
  assert.equal(gitHubMissing({ configured: false, enabled: true, error: "The saved GitHub connection could not be read." }), true);
  assert.equal(gitHubMissing({ configured: true, enabled: true, repository: "acme/site-s1" }), false);
  assert.equal(gitHubMissing({ configured: false, enabled: false }), false);
  // switched off for the whole platform, the store is reported off whatever it asks for
  assert.equal(gitHubMissing({ configured: false }), false);
});
