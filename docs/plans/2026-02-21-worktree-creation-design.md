# Worktree Creation from CCC

## Summary
CCC becomes the one-stop shop for creating git worktrees. No external tooling needed.
Worktree creation is woven into the existing session/group flows — zero extra steps.

## Decision
Option A: CCC runs `git worktree add` itself, giving full control over placement and grouping.

## Task Breakdown

Progress: 0% complete

- [ ] Add GitService with `CreateWorktree` and `IsGitRepo` methods
- [ ] Update PickDirectory to show "New worktree from..." section
- [ ] Add new group creation path: select repos → feature name → auto-create worktrees
- [ ] Generate .feature-context.json for new worktree groups
- [ ] Update CreateNewGroup menu to include "New worktrees" option

## Architecture

### Single session flow
PickDirectory shows grouped choices:
- Open directly: favorites as-is
- New worktree from...: favorites that are git repos
- Custom path / Cancel

Picking a worktree entry → prompt for branch name → `git worktree add` → session in worktree.

### Group flow
New option: "New worktrees (pick repos)"
1. MultiSelectionPrompt with favorites (git repos only)
2. TextPrompt for feature name
3. CCC creates `worktreeBasePath/{featureName}/{repoName}` for each
4. Generates `.feature-context.json`
5. Creates sessions + group

### GitService
- `IsGitRepo(path)` → checks for `.git`
- `CreateWorktree(repoPath, worktreeDest, branchName)` → `git worktree add`
- `GetDefaultBranch(repoPath)` → returns base branch for worktree
