# Security

## Reporting a problem

If you find a security problem in this project, please report it privately:
open the **Security** tab of this repository and choose **Report a vulnerability**.
Please don't post it in a public issue.

You'll get a reply as soon as possible, usually within a few days.

## Supported versions

Only the latest release gets fixes. The app's built-in updater installs it.

## How keys are handled

- No API keys, tokens or passwords are stored in this repository. Every push is scanned
  for them (see `.github/workflows/secret-scan.yml`), and GitHub push protection is on.
