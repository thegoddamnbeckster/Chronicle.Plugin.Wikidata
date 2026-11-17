# Chronicle.Plugin.Wikidata

[Wikidata](https://www.wikidata.org/) plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Wikidata is the structured side of Wikipedia: one item per movie, show, album, book, game or person, with the ids that same thing has at other
services. This plugin does three things with it:

- **Finds an item by an id Chronicle already has** (IMDb, TMDB, TheTVDB, TVmaze, MusicBrainz, Steam, IGDB, Open Library) and **passes the
  other ids it holds back to Chronicle**, so the other plugins can be matched without a name search.
- **Adds structured facts**: the name in other languages and every alias, release year, genres, the people and companies credited (with a
  logo where Wikidata has one), the item's image, logo and poster where it has them, official websites, and links to other sites.
- **Fills in a person** Chronicle already has, found by an id its credits carry: birth and death date (when Wikidata knows the day), the
  portrait, occupations, other-language names, and the ids it holds elsewhere. A person is never found by name.

**It works on its own.** No API key, no account, and no other Chronicle plugin has to be installed.

## Setup

1. Install the plugin in Chronicle (**Settings → Plugins**) and restart Chronicle once (plugins are registered at start-up).
2. After installing, add the plugin to the lists on **Settings → Metadata Assignment** (at the end to fill gaps, higher to prefer it).
   Installing does not put it on any list.
3. Nothing has to be entered. The optional **Contact detail** is for Wikimedia only (see Terms).

## What it fills

`movies`, `tv`, `music` (Artist, Album, Track), `book`, `game` and `people`:

| Field | From |
|-------|------|
| title | the item's label: English, else the language-neutral label, else any language |
| year | earliest publication date (else inception) |
| genres, tags | genre; instance-of and main subject; a person's occupations; a game's platforms, game modes and engine |
| cast | cast members with the character played and their portrait; a game's developers and voice actors (with the character voiced); a band's members |
| crew | directors, writers, producers, composers, cinematographers, editors, authors, performers, publishers, production companies, distributors, labels, broadcasters; a game's designers, programmers, creators and art directors |
| person | birth date and death date (only when known to the day), the portrait (the preferred image), occupations, other names |
| poster, logo | film poster only (an item's general image is offered as an additional image, never promoted: it can be a screenshot); logo image. Addresses on Wikimedia Commons, built only from file names Wikidata returned |
| other names | every label and every alias in every language |
| everything else | kept: the whole entity under `raw`, every external id by property, every sitelink |

## Ids

Every id is `wikidata:Q<number>`. Fix Match accepts `Q42`, `wikidata:Q42` and `wikidata.org` addresses, and refuses anything with extra
text after the number rather than trimming it.

An item is found by **an id, never by its name**, when Chronicle matches automatically: a wrong match from a common name is worse than
none. Add Media and Fix Match also search by name, and list only results whose one-line description names the right kind of thing.

The ids passed back for other plugins (only when the item holds exactly one of that kind): `imdb`, `tmdb`, `tvdb`, `tvmaze`,
`musicbrainz`, `steam`, `igdb`, `openlibrary`, `anilist`. Each goes in the form the receiving plugin accepts: TMDB, Simkl and Trakt take
only a number there and IMDb only a text id, and the host silently drops an id of the other kind. An item holding two of a kind (a TMDB movie id and a TMDB TV id) passes neither,
and an id that two Wikidata items share matches neither item.

## Limits, stated plainly

- **Slow on purpose.** Wikimedia asks for one request at a time, a second or more apart. A well-described item takes **3 to 8 seconds**
  (measured: 4 to 5 seconds); a large library takes a long time the first time.
- **Big records.** The whole entity is kept: about 130 to 250 KB for a well-described item, far less for a sparse one.
- **Coverage is uneven.** Wikidata holds what its volunteers entered. Many games and obscure titles have a name, a date and little else; most
  companies have **no logo**, and where one exists it is a single image, not a light and a dark version.
- **No prose.** Wikidata's one-line description is not an overview, so `overview` is not filled; the Wikipedia plugin supplies text.
- **A person is found by id only.** By IMDb name id (`nm…`) or TMDB person id; never by name, since two people share a name.
- **Nothing about the people is left out.** Every person in a cast or crew list, every voice actor and every member of a band is looked up in
  full (their own record, not just their name), so each credit carries the person's portrait when Wikidata has one and a `wikidata:` person id
  that fills in their dates and ids when they are looked up in turn. This costs requests: The Matrix (19 cast, 20 crew) takes about 5 seconds,
  a film with a hundred cast takes minutes, which is why the plugin declares a long call deadline and runs in the background.
- **Not every id is passed on for a person.** Only the IMDb name id and the TMDB person id. A person who is also a musician holds a
  MusicBrainz artist id, and passing it on would seed a music artist from a person.
- **Seasons and episodes** are found only by an id Wikidata holds for them (an IMDb episode id), never by name.
- **A block page.** If Wikidata answers with a web page instead of data, the plugin never tries to get past it: it stops and pauses all
  Wikidata requests for 30 minutes.

## Terms

Wikidata's structured data is released under **CC0**: no attribution or licence condition on the data. Images on Wikimedia Commons carry
their own licences, shown on each file's page; the plugin only offers their addresses. Every request names Chronicle, the plugin version and
the project's public address in its `User-Agent`, as Wikimedia asks. The optional contact detail you enter is added to it and goes nowhere else.

## Development

```bash
dotnet build
cd tests && dotnet test
WIKIDATA_LIVE=1 dotnet test --filter FullyQualifiedName~LiveTests   # three real lookups, paced
```

The tests use responses recorded from the real API (`tests/fixtures`). The plugin project references `..\Chronicle\src\Chronicle.Plugins`,
so the Chronicle repository must sit beside this one.
