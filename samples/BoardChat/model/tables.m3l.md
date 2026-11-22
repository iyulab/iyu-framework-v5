# Namespace: boardchat.sales

## OrderStatus ::enum
- paid: "Paid"
- pending: "Pending"
- refunded: "Refunded"

## CustomerSegment ::enum
- consumer: "Consumer"
- business: "Business"
- enterprise: "Enterprise"

## Timestampable ::interface
- created_at: timestamp = now()
- updated_at: timestamp = now()

---

## Region : Timestampable

- id: identifier @pk @generated
- name: string(60) @not_null "Region name"

## Product : Timestampable

- id: identifier @pk @generated
- name: string(80) @not_null "Product name"
- category: string(40) @not_null "Category"
- price: decimal(10,2) @not_null "List price"

## Customer : Timestampable

- id: identifier @pk @generated
- name: string(80) @not_null "Customer name"
- signup_date: date @not_null "Signup date"
- segment: CustomerSegment = consumer @not_null "Segment"
- email: string(120) @not_null "Email"

## Order : Timestampable

- id: identifier @pk @generated
- order_date: date @not_null "Order date"
- status: OrderStatus = pending @not_null "Status"
- total: decimal(12,2) @not_null "Order total"
- margin: decimal(12,2) @not_null "Margin"
- region_id: identifier @reference(Region) @not_null
- customer_id: identifier @reference(Customer) @not_null
- product_id: identifier @reference(Product) @not_null

- region_name: string @lookup(region_id.name) "Region"
- customer_name: string @lookup(customer_id.name) "Customer"
- product_name: string @lookup(product_id.name) "Product"
