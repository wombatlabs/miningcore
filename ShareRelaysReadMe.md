## ShareRelaysReadMe.md

**Composed by:** DankMiner  
**Updated by:** wombatlabs
**Inspiration:** Based on insights and studies from the miningcore GitHub repository.

---

### Purpose

The overall goal is to enable a setup where **multiple stratum servers** work in tandem with a **shared primary server**. This architecture allows us to:

- **Distribute load:** Different servers can handle mining requests concurrently.
- **Increase reliability:** If one stratum server goes down, the others continue to operate and feed data into the primary server.
- **Enhance security:** A shared encryption key ensures that communication between all parts of the system is protected.
- **Centralize management:** The primary server aggregates data from individual stratum servers, which simplifies monitoring and control.

---

### Configuration Details

#### On the Primary Server

In your primary server’s configuration file—typically `config.json`—you’ll add the section `"shareRelays"`. This is an array containing the details for each stratum server.

**Example:**

```json
"shareRelays": [
    {
        "url": "tcp://207.244.250.69:6000",
        "sharedEncryptionKey": "password"
    },
    {
        "url": "tcp://154.53.39.114:6000",
        "sharedEncryptionKey": "password"
    },
    {
        "url": "tcp://66.94.123.222:6000",
        "sharedEncryptionKey": "password"
    }
]
```

**Steps:**

1. **Replace the URL:** For each entry, change the IP address (`207.244.250.69`, `154.53.39.114`, `66.94.123.222`, etc.) with the actual IP address or domain name of the stratum server.
2. **Update the shared encryption key:** Change the value for `"sharedEncryptionKey"` from `"password"` to your unique key. This shared key is critical for ensuring that both the primary and stratum servers can authenticate each other securely.
3. **(optional) Enable internal stratum on primary:** Add `"enableInternalStratum": true,` to each coin's config like so:
```bash
"id": "btc",
"enabled": true,
"coin": "bitcoin",
"enableInternalStratum": true,
```

#### On Each Stratum Server

Similarly, on each individual stratum server, you must configure a corresponding section, labeled `"shareRelay"`. This tells the server where to publish its data and what key to use for secure communication.

**Example:**

```json
"shareRelay": {
    "PublishUrl": "tcp://207.244.250.69:6000",
    "SharedEncryptionKey": "password"
}
```

**Steps:**

1. **Update the Publish URL:** Change the `PublishUrl` to reflect that server's public IP address or hostname.
2. **Ensure Matching Encryption:** The `"SharedEncryptionKey"` must match the encryption key specified on the primary server for that relay. This ensures that both ends can securely exchange data.

---

### Why Do We Do This?

1. **Centralized Data Collection:**  
   By linking multiple stratum servers to a single primary server, mining data is gathered and consolidated in one place. This makes it easier to analyze performance, detect issues, and manage operations without having to access each server individually.

2. **Enhanced Security:**  
   The use of a shared encryption key across all communications is vital. It ensures that data transferred between the servers is encrypted, thwarting unauthorized access and potential tampering. This is especially important in mining operations where sensitive data is exchanged.

3. **Load Balancing and Fault Tolerance:**  
   Having a distributed network of stratum servers can handle a larger volume of mining requests and distribute the load evenly. If one server encounters problems or needs maintenance, the primary server continues to receive data from the remaining nodes, ensuring continuity.

4. **Scalability:**  
   This configuration is scalable. As mining operations grow, additional stratum servers can be added with minimal adjustments—just update the primary server’s configuration and add a corresponding `shareRelay` on the new node. This seamless scalability helps accommodate growth without significant re-engineering.

---

In summary, this design is a robust method to manage multiple mining servers. By ensuring a secure, scalable, and centralized approach, you maintain high efficiency and security across your mining network. Additionally, this system design helps in balancing loads and provides redundancy, which is crucial for uninterrupted mining operations.

---

**Extra Insights:** If you’re looking to further optimize your mining setup, consider integrating automated monitoring tools on your primary server. These tools can alert you immediately if a relay goes offline or encounters errors, allowing for rapid intervention. Moreover, periodically updating your encryption keys and rotating them on a scheduled basis can add an extra layer of security to your communication channels.
