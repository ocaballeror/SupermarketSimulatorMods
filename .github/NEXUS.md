# Nexus upload workflow

`workflows/nexus-upload.yaml` runs on every push to master/main. It finds the mods whose csproj `<Version>` is newer than the latest version uploaded on Nexus (`tools/nexus-outdated.sh`, whatever commit changed it), builds each, zips `BepInEx/plugins/<Mod>.dll`, and uploads it as a new version of the mod's Nexus file, with `description.md` (written in BBCode, not markdown) as the file description. Mods that are not newer than Nexus are untouched.

## One-time setup
1. **Secrets** (repo Settings > Secrets and variables > Actions):
   - `NEXUS_MODS_API_KEY`: your Nexus API key.
   - `INTEROP_REPO_TOKEN`: a fine-grained personal access token with read-only "Contents" access to `ocaballeror/interop-dlls` (the private repo; the default `GITHUB_TOKEN` can't read other repos).
2. **Game assemblies:** the workflow downloads `interop.zip` from the `INTEROP_TAG` release of `ocaballeror/interop-dlls` (a zip with an `interop/` folder) and `BepInEx/core` from the public BepInEx be.785 build, then builds with `-p:GameDir`. After a game update, regenerate `BepInEx/interop`, upload a new release and change `INTEROP_TAG` in the workflow.
3. **URLs:** put each mod's Nexus page URL in `nexus-mods.json` (`https://www.nexusmods.com/<game>/mods/<n>`). The file id, file name and internal mod id are looked up by `tools/nexus-resolve.sh`, assuming the page has one active file that receives each new version. Mods with an empty URL are skipped with a warning.
4. Manual run: Actions > the workflow > Run workflow, optionally listing mod folders to force.

## Limits
- The Nexus v3 API used by the action has no endpoint to edit the mod *page* description. `description.md` goes into the **file version description**; update the page text by hand.
