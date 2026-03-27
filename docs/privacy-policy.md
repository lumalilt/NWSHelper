# NWS Helper Privacy Policy

Last updated: 2026-03-26

## Scope

This privacy policy describes how NWS Helper handles information when you use:

- the desktop application distributed through direct download or Microsoft Store
- account link and Store purchase restore features
- direct activation and entitlement validation features

This policy is intended to cover the current product behavior reflected in the codebase as of the date above. NWS Helper is a desktop utility for territory/address workflow processing. Address extraction, boundary matching, and most file processing happen locally on your device.

## Information NWS Helper Processes

### Information you enter

NWS Helper may process information that you provide directly, including:

- email address used for account link and Store purchase restore
- direct activation key, if you use direct licensing instead of Store entitlement restore
- support information you choose to share separately through public support channels

### Device and app information sent for licensing and account-link operations

When you use account link, Store restore, or direct activation, NWS Helper may send limited technical information required to operate those features, including:

- an install-bound hashed identifier derived from the local device and user context
- app version
- whether the app is running as a Microsoft Store install
- packaged/runtime context used to determine Store servicing behavior

When Store restore is used, NWS Helper may also send a Store proof envelope containing limited Microsoft Store runtime information, such as:

- whether the app appears to be packaged as a Store install
- package family name, when available
- a limited process-path hint
- detection source and proof level

If real Microsoft Store ownership verification is enabled in a future release, NWS Helper may also send limited ownership evidence needed to verify the exact paid Store product or add-on, such as:

- Store product identifier
- in-app offer token, if applicable
- SKU identifier
- owned/trial status
- expiration timestamp, if applicable
- verification source

### Information stored locally on your device

NWS Helper stores local configuration and entitlement cache data to make the app usable across sessions. Depending on which features you use, local storage may include:

- theme, setup, and update preferences
- account-link cache data such as account ID, email, purchase source, linked/sync timestamps, and last error
- entitlement cache data such as base plan, add-on codes, expiration, validation source, and signed entitlement token
- activation key and activation key hash for direct licensing flows

Store-to-direct migration backups intentionally exclude direct activation keys and signed entitlement tokens.

### Information NWS Helper does not upload as part of account-link or entitlement flows

NWS Helper's local processing inputs and outputs, including territory files, address files, extracted results, and similar working data, are not uploaded as part of the account-link, Store restore, or direct activation requests described in this policy.

## How NWS Helper Uses Information

NWS Helper uses the information above to:

- send sign-in links for account link flows
- associate an installation with an account-link record
- restore or verify Store-linked entitlement state
- validate direct activation keys and signed entitlement state
- cache entitlement status locally so the app can continue working between validations
- support Store-to-direct migration and entitlement rehydration on a new install
- troubleshoot activation, account-link, and entitlement problems when you request support

## Sharing and Service Providers

NWS Helper may send the information described above to services used to operate licensing and account-link functionality, including:

- Microsoft Store or related Microsoft licensing surfaces when Store entitlement verification is used
- Supabase-hosted backend endpoints used for activation, account link, entitlement status, and Store claim handling

NWS Helper does not use the current account-link and entitlement flows to sell your personal information.

Based on the current codebase, NWS Helper does not include third-party advertising analytics or crash-reporting SDKs in the desktop app runtime.

## Data Retention

Local settings and cached entitlement/account-link data remain on your device until you remove them, reset the app state, or uninstall the app.

Backend account-link and entitlement records may be retained as needed to operate licensing, restore entitlement state, investigate abuse, and provide support. Retention periods may change as the service matures or legal/operational requirements become clearer.

## Your Choices

You can choose not to use:

- account link
- Restore Store Purchase
- direct activation

If you do not use those features, the related email address, activation-key, and bridge/account-link requests will not be sent.

You can also clear cached account-link state locally. If you uninstall the app or remove local app data, locally cached settings and entitlement state on that device may be removed.

## Children's Privacy

NWS Helper is not directed to children.

## Changes to This Policy

This policy may be updated as NWS Helper's Store, licensing, and cross-channel entitlement features evolve. The latest published version should be used for Microsoft Store listing and submission links.

## Contact and Support

For support requests or privacy questions related to NWS Helper, use the public support guidance for the product:

[SUPPORT.md](../SUPPORT.md)

Do not post license keys, client secrets, signing certificates, or other credentials in public support channels.