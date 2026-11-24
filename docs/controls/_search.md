# Search Control Descriptor

This control enables dynamic data retrieval based on user input. It's especially useful for querying product data, verifying inputs, or enriching content blocks with data fetched from external APIs.

It performs one or more backend requests and stores the result in a block's value, making it available for display or further logic.

## Configuration options

| Property       | Type                                         | Description                                                                                       |
| -------------- | -------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| `request`      | OptionsRequest                               | A single request to be executed, described as the [select control](select.md#optionsrequest) describes it.<br>`response.result` is a dotted path to the part of the response to keep. |
| `requests`     | { [key: string]: OptionsRequest }            | Use this to define **multiple requests**, executed sequentially.<br>Ignored if **request** is present. |
| `displayInfo`  | DisplaySearchResult[]                        | Defines how the response data is displayed inside the control: a row per item with its `label`, the `path` to the value, and `type`, `text` (default) or `image`. |
| `nodataText`   | string                                       | Text shown if the request returns no data.                                                        |
| `button`       | boolean <br> string                          | Shows a button that triggers the search, with this text or, for **true**, with the default text.<br>Without it, the search is triggered on input change. |
| `debounceTime` | number                                       | Time in milliseconds between the last change of the input and the search. Default is `1000`.     |


!!! info
    * When a `button` is present, the control shows the button only, and the request runs with an empty query. Without `button`, the request is triggered as the user types, with the typed text as `{{__searchQuery}}`.<br>
    * The value of the control is `{ __nodata, __searchQuery, value }`. The result of a single `request` is saved to the `value` property of the control.<br>
    * When using `requests`, each request's result is stored under a named key.<br>
    * Sequential execution of `requests` allows one result to be used in the next request (e.g., pass product ID from the first to the second request as `{{item.product.id}}`).<br>
    * The designer shows the result of `request` only: the `displayInfo` rows, which read their `path` in `value`, or the JSON of `value` without them.


## Single request example

Performs one API call to retrieve product details using a SKU.

??? Example "Single request example"
    ```json
    ...
        "settings": [
            {
                "id": "product",
                "label": "SKU",
                "sort": 1,
                "type": "search",
                "nodataText": "Search by SKU to retrieve product data here",
                "default": {
                    "value": {
                        "id": "9cbd8f316e254a679ba34a900fccb076",
                        "code": "3DRSOLO",
                        "name": "3DR Solo Quadcopter (No Gimbal)",
                        "imgSrc": "/themes/assets/blocks/solo-quadcopter.jpg"
                    },
                    "__nodata": false,
                    "__searchQuery": "3DRSOLO"
                },
                "displayInfo": [
                    {
                        "label": "Name",
                        "path": "name"
                    },
                    {
                        "label": "Image",
                        "path": "imgSrc",
                        "type": "image"
                    }
                ],
                "request": {
                    "url": "/graphql",
                    "method": "post",
                    "body": {
                        "operationName": null,
                        "variables": {},
                        "query": "{products(storeId:\"{{location.params.storeId}}\",filter:\"sku:{{__searchQuery}}\"){items{id,code,name,imgSrc}}}"
                    },
                    "response": {
                        "result": "data.products.items.0"
                    }
                }
            },
            ...
        ]
    ...
    ```


## Multiple requests example

Uses two API calls: one to get the product by SKU, and another to fetch its price by product ID.

??? Example "Multiple request example"
    ```json
    ...
        "settings": [
            {
                "id": "product",
                "label": "SKU",
                "sort": 1,
                "type": "search",
                "nodataText": "Search by SKU to retrieve product data here",
                "default": {
                    "product": {
                        "id": "9cbd8f316e254a679ba34a900fccb076",
                        "code": "3DRSOLO",
                        "name": "3DR Solo Quadcopter (No Gimbal)",
                        "imgSrc": "/themes/assets/blocks/solo-quadcopter.jpg"
                    },
                    "price": {
                        "formattedAmount": "$995.99"
                    },
                    "__nodata": false,
                    "__searchQuery": "3DRSOLO"
                },
                "requests": {
                    "product": {
                        "url": "/graphql",
                        "method": "post",
                        "body": {
                            "query": "{products(storeId:\"{{location.params.storeId}}\",filter:\"sku:{{__searchQuery}}\"){items{id,code,name,imgSrc}}}"
                        },
                        "response": {
                            "result": "data.products.items.0"
                        }
                    },
                    "price": {
                        "url": "/graphql",
                        "method": "post",
                        "body": {
                            "query": "{product(id:\"{{item.product.id}}\",storeId:\"{{location.params.storeId}}\",cultureName:\"{{settings.cultureName}}\",currencyCode:\"USD\"){price{actual{formattedAmount}}}}"
                        },
                        "response": {
                            "result": "data.product.price.actual"
                        }
                    }
                }
            },
            ...
        ]
    ...
    ```


!!! info

    * Each request result is stored under its key (`product`, `price`), accessible in subsequent requests as `{{item.<key>}}`.

See [Schemas](../contracts/preview-protocol.md#schemas) in the preview protocol for the requests a schema can make.

<br>
<br>
********

<div style="display: flex; justify-content: space-between;">
    <a href="../paragraph">← Paragraph </a>
    <a href="../select">Select →</a>
</div>