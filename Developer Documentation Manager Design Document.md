# Developer Documentation Manager: Design Document

Sep 29, 2026 · @Sean

## Overview and goals

The Developer Documentation Manager (DDM) is one system of record where teams store, version and read the documentation that belongs to each project: markdown pages, images, and OpenAPI specifications, all reachable through a web client and a REST API.

The problem it solves is scatter. Guides live in repos, diagrams in chat threads, and API specs in whichever folder the last engineer used. Nothing ties them to a project, and nothing tells a reader which version is current.

**Primary users**

- Developers who write and maintain docs, usually in markdown, often from CI.
- Consumers of a project's APIs who need a readable, current reference.
- Platform or docs owners who manage projects, access and tags.

**Goals**

1. Every document, asset and spec belongs to exactly one project, and is found through that project.
2. Anything the web client can do, the API can do, so docs can be published from CI.
3. OpenAPI specs render as interactive reference next to the prose that explains them.
4. Content is versioned, so a reader can see what changed and return to an earlier state.
5. Tags and search work across projects for people who have access to more than one.

**Success looks like** a team publishing docs from a pipeline on merge, and a new developer finding the right guide, image and API reference in one place within a minute.

## Requirements

The MVP must store and serve three content types, scope every item to a project, and expose the same operations through a web client and a REST API.

**Functional requirements**

| ID | Requirement | Priority |
| --- | --- | --- |
| F1 | Create, read, update, delete and list projects | Must |
| F2 | Create and edit markdown documents in a project, with nested folders or a page tree | Must |
| F3 | Upload, store and serve images and other binary assets, referenced from markdown | Must |
| F4 | Upload OpenAPI 3.x specs (YAML or JSON), validate them, and render interactive reference | Must |
| F5 | Tag projects, documents, assets and specs; filter by tag | Must |
| F6 | Full-text search within a project and across permitted projects | Should |
| F7 | Version history for documents and specs, with diff and restore | Should |
| F8 | API tokens for CI publishing; a bulk publish endpoint that syncs a docs folder | Should |
| F9 | Roles per project: reader, editor, admin | Must |
| F10 | Webhooks on publish, for rebuilds and notifications | Could |

**Non-functional requirements**

- **Availability and durability:** content is never lost on a failed write; uploads are atomic from the reader's view.
- **Performance:** a rendered page loads in under 500 ms at the 95th percentile for cached reads; search returns in under 1 s. These are starting targets to confirm.
- **Security:** untrusted markdown and SVG are sanitized before render; uploads are type- and size-checked and scanned where possible.
- **Portability:** documents remain plain markdown with front matter, so content can be exported without loss.
- **Operability:** structured logs, metrics and health checks from the first release.

**Non-goals for now:** real-time collaborative editing, a WYSIWYG editor, and generating docs from source code.

## Domain model

Project is the root of ownership: every document, asset and spec belongs to one project, and access is granted at that level. Content items share a common shape so tags, versions and search work the same way for all three.

| Entity | Purpose | Key fields |
| --- | --- | --- |
| Project | Unit of ownership and access | id, slug, name, description, visibility |
| Member | Links a user to a project with a role | project\_id, user\_id, role |
| Document | A markdown page | id, project\_id, path, title, front\_matter, current\_version\_id |
| Asset | An image or other binary | id, project\_id, path, content\_type, size, checksum, storage\_key |
| Spec | An OpenAPI definition | id, project\_id, name, api\_version, format, current\_version\_id |
| Version | An immutable snapshot of a Document or Spec | id, item\_id, number, content\_ref, author, created\_at, message |
| Tag | A label, scoped to a project or global | id, name, scope |
| TagAssignment | Attaches a tag to any content item | tag\_id, item\_type, item\_id |
| ApiToken | Machine credential for CI | id, project\_id, scopes, hashed\_secret, expires\_at |

**Design choices**

- **Paths, not only ids.** Documents and assets have a path unique within a project (for example `guides/setup.md`, `images/flow.png`), so relative links in markdown resolve without lookups by id.
- **Immutable versions.** Editing creates a new Version; the item points at its current one. Assets are content-addressed by checksum, so identical uploads are stored once.
- **One tag table.** Tags attach polymorphically to any item type, which keeps filtering and search uniform.

## System architecture

The DDM starts as one API service with clear internal modules, backed by PostgreSQL and object storage, with background workers for everything slow.

&#91;embedded content: system architecture · 2 clients, 1 API service, 3 stores, workers\]

Every request passes the identity module before reaching content. A write commits to PostgreSQL and object storage first, then an event drives search indexing, rendering and webhooks, so a slow job never blocks a save. Each module talks to the others through an internal interface, which is what lets search or assets become separate services later without a rewrite.

## API design

The API is a versioned REST interface under `/api/v1`, with every content route nested under a project. The web client is its first consumer, so nothing is available in the UI that the API cannot do.

| Resource | Endpoints | Notes |
| --- | --- | --- |
| Projects | `GET/POST /projects`, `GET/PATCH/DELETE /projects/{slug}` | Listing returns only projects the caller can see |
| Documents | `GET/POST /projects/{slug}/docs`, `GET/PUT/DELETE /projects/{slug}/docs/{path}` | `GET` returns markdown or rendered HTML by `Accept` header |
| Assets | `POST /projects/{slug}/assets`, `GET/DELETE /projects/{slug}/assets/{path}` | Upload is multipart, or a pre-signed URL for large files |
| Specs | `GET/POST /projects/{slug}/specs`, `GET/PUT/DELETE /projects/{slug}/specs/{name}` | Validated on write; errors returned as a list with line numbers |
| Versions | `GET /projects/{slug}/docs/{path}/versions`, `POST .../versions/{n}/restore` | Same shape for specs |
| Tags | `GET /tags`, `PUT /projects/{slug}/{type}/{path}/tags` | Replaces the tag set for one item |
| Search | `GET /search?q=&project=&tag=&type=` | Cross-project only across projects the caller can read |
| Publish | `POST /projects/{slug}/publish` | Accepts an archive of a docs folder and syncs it in one transaction |
| Tokens | `GET/POST/DELETE /projects/{slug}/tokens` | Secret shown once at creation |

**Conventions**

- **Auth:** `Authorization: Bearer` with a user session token or an API token. Scopes limit an API token to one project and to read or write.
- **Concurrency:** writes send `If-Match` with the current version's ETag; a stale write returns `412` rather than overwriting.
- **Errors:** RFC 9457 problem details, with a stable `code` field.
- **Pagination:** cursor-based, `limit` and `cursor` parameters, with a `next` cursor in the response.
- **Self-description:** the DDM API publishes its own OpenAPI spec, and we host it in a DDM project as the first dogfood case.

**Example: publish a page from CI**

```http
PUT /api/v1/projects/payments/docs/guides/setup.md
Authorization: Bearer ddm_tok_…
If-Match: "v7"
Content-Type: text/markdown

---
title: Setup
tags: [onboarding, guide]
---
# Setup
…
```

Front matter in the markdown supplies title and tags, so a docs folder in a repo can publish with no extra metadata calls.

## Web client

The client is a single-page app organised around the project: a reader picks a project, then browses its pages, assets and API reference from one sidebar.

| Screen | What the user does | Key behaviour |
| --- | --- | --- |
| Project list | Find and open projects; filter by tag | Shows only permitted projects; recent and pinned first |
| Project home | See the page tree, recent changes and API specs | Landing page is the project's `index.md` when present |
| Document view | Read a rendered page | Table of contents, tag chips, version selector, relative links and images resolve inside the project |
| Editor | Edit markdown with live preview; drag in images | Saves as a new version; warns on a stale base version |
| Asset library | Browse, upload, replace and copy links to images | Shows where each asset is referenced |
| API reference | Read a spec as endpoints, schemas and examples | Try-it-out is off by default and needs an explicit target server |
| Search | Query with project, tag and type filters | Results grouped by project, with highlighted snippets |
| Project settings | Manage members, tags and API tokens | Admin role only |

**Rendering**

- **Markdown:** rendered server-side to sanitized HTML and cached, with support for tables, fenced code with syntax highlighting, and admonitions. Diagrams (Mermaid) are a later addition.
- **OpenAPI:** rendered by an embedded viewer library rather than custom code in the first release; the choice is listed under technology options.
- **Images:** served through the API with long-lived caching keyed by checksum, so a changed image gets a new URL.

**Editing model:** a plain markdown editor with preview is enough for the MVP. Git-style publishing through the API covers teams that prefer to write docs in their repository.

## Storage and processing pipeline

Metadata goes in a relational database and file bodies go in object storage, so each store does what it is good at. Every write passes through a validation step for its content type before it is committed.

| Content | Stored in | On write | On read |
| --- | --- | --- | --- |
| Markdown | Body in object storage, metadata and current-version pointer in the database | Parse front matter, extract links, images and headings; sanitize; index for search | Serve cached rendered HTML; raw markdown on request |
| Images and files | Object storage, keyed by project and checksum | Check type against an allow-list and size limit; compute checksum; optionally generate thumbnails | Stream through the API or a signed URL, with immutable cache headers |
| OpenAPI specs | Body in object storage, parsed summary in the database | Validate against the OpenAPI schema; resolve `$ref`s; reject on errors; extract endpoints and tags for search | Serve the original document and a normalised JSON form to the viewer |

**Write path**

1. The client sends content to the API, which authenticates the caller and checks their project role.
2. The API validates and sanitizes the content and writes the body to object storage.
3. In one database transaction it creates the Version row, moves the current-version pointer and records tag assignments.
4. After commit, an event is published; workers refresh the search index, render caches and thumbnails, and fire webhooks.

Step 4 is asynchronous on purpose: a slow index or renderer never blocks a save, and a failed job is retried without affecting the content.

**Security controls**

- Sanitize rendered HTML against an allow-list, and serve user-supplied SVG as a download or through a restrictive content security policy.
- Never trust the uploaded content type; sniff the bytes.
- Cap spec size and `$ref` depth, and block external `$ref` resolution by default to avoid server-side request forgery.
- Serve user content from a separate domain from the app so scripts in a document cannot reach session cookies.

## Tagging, search and versioning

Three cross-cutting features work the same way on documents, assets and specs, which is why they share one item shape.

**Tagging.** Every item belongs to a project already, so tags add a second, free-form dimension: `onboarding`, `deprecated`, `v2`, `internal`. Tags are project-scoped by default, with an optional global set that admins curate so names do not drift. Markdown front matter can declare tags, and the API and UI can edit them. Filtering combines tags with AND by default.

**Search.** The MVP uses the database's built-in full-text search over titles, headings, body text and spec endpoints, filtered by project, tag and type. Results are filtered by permission at query time, so a user never sees a snippet from a project they cannot read. If volume or relevance outgrows this, a dedicated search engine takes over behind the same API, which is the reason search sits behind its own service boundary.

**Versioning.** Each save creates an immutable Version with an author and an optional message. Readers can list versions, view a diff of markdown or spec text, and restore an old version, which creates a new version rather than rewriting history. Assets are replaced by uploading a new file at the same path; the old object is kept for as long as any version references it.

**Open choice:** whether a project can also be *published* as a named release (for example `v2.1`) that freezes a set of versions, so consumers can read docs as they were at a product release. This adds a Release entity, and we recommend deferring it to phase 3.

## Authentication and access

Access is decided per project, using three roles and one visibility setting, and enforced in the API layer for every route including search.

| Role | Read | Create and edit content | Manage members, tags, tokens, settings |
| --- | --- | --- | --- |
| Reader | Yes | No | No |
| Editor | Yes | Yes | No |
| Admin | Yes | Yes | Yes |

Project visibility is either **private** (members only) or **internal** (any signed-in user can read). A public setting for unauthenticated readers is an open question, listed at the end.

**Identity**

- **People:** sign in through OpenID Connect against the organisation's identity provider, with a local-account fallback only for development. The session is a short-lived token with refresh.
- **Machines:** API tokens are scoped to one project and to read or write, stored only as a hash, shown once, and revocable. CI publishes with a write token.
- **Audit:** every write and permission change records who, what and when; the audit log is admin-readable per project.

**Enforcement.** A single authorization module answers can this principal do this action on this project, and every handler calls it. Search applies the same check by filtering to permitted project ids before ranking, rather than filtering results afterwards.

## Architecture path

The path runs in four phases from a working core to scale, and each phase ends in a gate, so the architecture grows only when evidence demands it.

&#91;embedded content: architecture path · 4 phases, 3 gates\]

Phase 1 proves the model end to end with the least infrastructure. Later phases add capability or swap one component behind an existing interface, and none of them requires rewriting the data model.

## Technology options and recommended stack

We recommend a modular monolith on PostgreSQL and S3-compatible object storage, because it delivers every MVP requirement with the fewest moving parts and leaves clear seams to split later. The specific language is a team-skills decision; the table shows the choices that matter most.

| Decision | Options | Recommendation | Why |
| --- | --- | --- | --- |
| Service shape | Modular monolith; microservices | Modular monolith | One deployable, one database transaction per write, and module boundaries that match the future service seams |
| Metadata store | PostgreSQL; MySQL; document DB | PostgreSQL | Relational integrity for projects and versions, plus built-in full-text search and JSON columns |
| File store | S3-compatible object storage; database blobs; local disk | S3-compatible | Cheap, durable, signed URLs, and works locally with MinIO |
| Search | PostgreSQL full-text; OpenSearch or Meilisearch | PostgreSQL first | No extra system at MVP; the search module interface allows a swap |
| Async work | Database-backed queue; Redis queue; managed queue | Database-backed queue first | Transactional outbox avoids losing events; move to a broker when volume demands |
| Web client | React SPA; server-rendered pages | React SPA with a typed API client | Editor and search are interactive; the client is generated from our own OpenAPI spec |
| Markdown rendering | Server-side pipeline; client-side rendering | Server-side, cached | Consistent output, sanitization in one place, and fast first paint |
| OpenAPI viewer | Embedded open-source viewer; custom renderer | Embedded viewer | Weeks of work saved; custom rendering can follow if the viewer limits us |
| Hosting | Containers on a managed platform; VMs; serverless | Containers on a managed platform | Simple to run, horizontally scalable, and portable |

The most consequential choice is the first: starting as a monolith with strict module boundaries (content, assets, specs, search, identity) is cheaper to build and reverse than starting distributed.

## Risks, open questions and next steps

The biggest risks are security of user-supplied content and scope growth into a full editor, so both get explicit controls.

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Malicious markdown, SVG or spec content (script injection, server-side request forgery through `$ref`) | Account or data compromise | Sanitize on render, separate content domain, block external refs, cap sizes |
| Search leaks content across projects | Confidential docs exposed | Filter by permitted projects before ranking; test this path explicitly |
| Scope creep into a WYSIWYG or collaborative editor | Delays the MVP | Non-goal for MVP; publishing from Git covers heavy authors |
| Large or malformed OpenAPI specs slow the viewer | Poor reading experience | Validate and normalise on write; lazy-load large schemas |
| Migrating existing docs is manual | Low adoption | Provide the bulk publish endpoint and an import script early |

**Open questions for you**

- Who are the readers: only your own developers, or partners and the public too? This decides whether a public visibility setting is needed.
- Roughly how many projects, documents and specs at launch and after a year? This decides whether PostgreSQL search is enough.
- Is there an existing identity provider to sign in against?
- Must docs live in Git as the source of truth, with the DDM as a publishing target, or is the DDM the source of truth?
- Are there compliance needs, such as data residency or audit retention?

**Next steps**

- [ ] Confirm the open questions above
- [ ] Choose the implementation language and hosting platform
- [ ] Write the OpenAPI spec for the DDM API and review it before coding
- [ ] Build phase 1, then run it on the DDM's own documentation
