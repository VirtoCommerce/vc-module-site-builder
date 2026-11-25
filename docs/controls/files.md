# Files Control Descriptor

This control lets users pick a file in the Site Builder asset library or enter the file URL.

| Property                 | Type                  | Description                                                                                                                            |
| ------------------------ | --------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `accept`                 | string              | Comma-separated list of accepted file extensions (`.pdf`) or MIME types (`application/pdf`, `image/*`).<br>The asset library shows only the matching files. |
| `multiple`               | boolean               | Allows multiple files. Default is `false`.                                                                                 |

The value of this control is a file URL, or an array of URLs when `multiple` is `true`. The files of a multiple control can be reordered and removed.

## Examples
### Single file


```json
...
    "settings": [
        {
            "id": "attachment",
            "label": "Attach a file",
            "type": "files",
            "multiple": false,
            "accept": ".pdf,application/pdf"
        },
        ...
    ]
...
```

**Result**

```json
...
    "content": [
        {
            "attachment": "https://localhost:5001/cms-content/Pages/B2B-store/assets/pages/contract.pdf"
            ...
        },
        ...
    ]
...
```



### Multiple files

```json
...
    "settings": [
        {
            "id": "attachments",
            "label": "Attach files",
            "type": "files",
            "multiple": true,
            "accept": ".pdf,application/pdf"
        },
        ...
    ]
...
```

**Result**

```json
...
    "content": [
        {
            "attachments": [
              "https://localhost:5001/cms-content/Pages/B2B-store/assets/pages/2-requerimento.pdf",
              "https://localhost:5001/cms-content/Pages/B2B-store/assets/pages/89e49b95-98e5-43fe-a250-3746af0660bf.pdf",
              "https://localhost:5001/cms-content/Pages/B2B-store/assets/pages/6268a3827af6a8c184ce400727.pdf",
              ...
            ]
            ...
        },
        ...
    ]
...
```

<br>
<br>
********

<div style="display: flex; justify-content: space-between;">
    <a href="../color-token">← Color token </a>
    <a href="../header">Header →</a>
</div>