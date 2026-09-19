# Security Policy

Frontly is private, production infrastructure. It answers live telephone calls on behalf of real
businesses and stores what those calls contain: recordings, transcripts, customer records,
appointments, and billing details belonging to people who never agreed to have their data handled
by anyone but the business they rang.

That is the reason for everything below. The cost of a probe here is not paid by us.

## No authorization to test

No permission is granted to any person or system to probe, scan, enumerate, fuzz, brute-force,
exploit, or otherwise attempt access to Frontly's systems. This covers production and staging, the
API, the web application, the webhook and live-call tool endpoints, the telephony integration, and
the database.

There is no bug bounty programme and no open testing window. Access to this source code is not
authorization to attack the service it describes, and neither is the absence of a technical control
that would have stopped you.

This applies equally to automated and autonomous systems: scanners, crawlers, agentic tooling, and
AI coding or research assistants, operating with or without a person watching. Whoever points such a
tool at Frontly is responsible for what it does — delegation to a model is not a defence, and
"the agent decided to" is not a distinction we recognise.

To be plain about what this file is: it is a statement of authorization, not a security control. It
defines what is permitted. It cannot and does not enforce anything, and it is not a substitute for
the controls that do.

## If you found something anyway

Incidental discovery happens. Report it, in confidence, to **security@frontly.org**.

Please do:

- Report privately and promptly, before telling anyone else.
- Include enough detail to reproduce it: endpoint, request, expected versus actual behaviour.
- Stop the moment you have confirmed the issue exists.

Please do not:

- Access, alter, retain, or remove data that is not yours.
- Pivot further into the system, or use one finding to reach another.
- Degrade, overload, or interrupt service for callers or tenants.
- Publish or demonstrate the issue before it is fixed.
- Attach a payment demand to the report. That is not disclosure.

**Personal data.** Call recordings and transcripts carry real people's voices, names, addresses, and
health or household details. If you encounter any, stop immediately, retain nothing, and say so in
your report so we can assess the exposure. Deliberate collection ends any presumption of good faith.

## Third-party services

Frontly is built on services it does not control, including Retell AI, Stripe, and its telephony and
hosting providers. Vulnerabilities in those products belong to their vendors — please report them
through the vendor's own disclosure programme, not here. Report to us only where Frontly's own
integration of them is at fault.

## What to expect

A good-faith report will be acknowledged, investigated, and answered. We will tell you what we found
and when it was fixed, and credit you if you would like to be credited and the report was your own
work. A report made in good faith and within the limits above will not be pursued.

## Maintainer

This project is maintained by Kawshik Barua. Security correspondence goes to security@frontly.org
rather than to any personal or public channel, including the issue tracker.
