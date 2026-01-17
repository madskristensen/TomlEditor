[marketplace]: https://marketplace.visualstudio.com/items?itemName=MadsKristensen.TomlEditor
[vsixgallery]: http://vsixgallery.com/extension/TomlEditor.4e804652-a783-473b-827f-6e41f5a48b9b/
[repo]:https://github.com/madskristensen/TomlEditor

# TOML Editor for Visual Studio

[![Build](https://github.com/madskristensen/TomlEditor/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/TomlEditor/actions/workflows/build.yaml)

**First-class TOML support in Visual Studio** — edit your configuration files with confidence.

[**Install from Visual Studio Marketplace**][marketplace] | [CI build][vsixgallery]

![TOML in Visual Studio](art/screenshot.png)

## Features

✅ **Syntax Highlighting** — Beautiful colorization for keys, values, tables, and comments  
✅ **Real-time Validation** — Catch errors as you type with instant feedback  
✅ **Code Outlining** — Collapse and expand sections for easier navigation  
✅ **Commenting** — Toggle comments with `Ctrl+/` or `Ctrl+K, Ctrl+C`  
✅ **Formatting** — Format Document (`Ctrl+K, Ctrl+D`) and Format Selection  
✅ **Smart Indentation** — Automatic indentation when pressing Enter  
✅ **Brace Matching** — Highlights matching brackets and quotes  
✅ **JSON Schema Support** — Validation and IntelliSense powered by JSON Schema  
✅ **Lightweight & Fast** — Designed for performance with zero impact on your workflow  

Whether you're working with Cargo.toml, pyproject.toml, or any other TOML configuration, this extension makes editing a breeze.

## JSON Schema Support

Enable schema-based validation and IntelliSense by adding a `#:schema` directive at the top of your TOML file:

```toml
#:schema https://json.schemastore.org/pyproject.json

[project]
name = "my-project"
version = "1.0.0"
```

### What You Get

- **Validation** — Errors and warnings when your TOML doesn't match the schema
- **IntelliSense** — Autocomplete for keys and enum values
- **QuickInfo** — Hover over keys to see descriptions, types, and allowed values

### Finding Schemas

Many popular TOML formats have schemas available at [SchemaStore.org](https://www.schemastore.org/json/):

| File | Schema URL |
|------|------------|
| `pyproject.toml` | `https://www.schemastore.org/pyproject` |
| `Cargo.toml` | `https://www.schemastore.org/cargo` |
| `.rustfmt.toml` | `https://www.schemastore.org/rustfmt` |
| `netlify.toml` | `https://www.schemastore.org/netlify` |
| `bacon.toml` | `https://dystroy.org/bacon/.bacon.schema.json` |

You can also use `file://` URLs to reference local schema files.

## Getting Started

1. Install the extension from the [Visual Studio Marketplace][marketplace]
2. Open any `.toml` file
3. Start editing with full language support!

## How can I help?

If you enjoy using this extension, please give it a ⭐⭐⭐⭐⭐ rating on the [Visual Studio Marketplace][marketplace] — it really helps others discover it!

Found a bug or have a feature request? Head over to the [GitHub repo][repo] to open an issue.

Pull requests are welcome! This is a personal passion project, so contributions are greatly appreciated.

You can also [sponsor me on GitHub](https://github.com/sponsors/madskristensen) to support continued development.