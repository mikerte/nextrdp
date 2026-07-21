package nextrdp.phase2

import rego.v1

default decision := {
  "allow": false,
  "reason": "Central policy denied the request.",
  "rules": ["phase2-default-deny"],
}

decision := {
  "allow": true,
  "reason": "The authenticated request satisfies the Phase 2 central policy.",
  "rules": ["phase2-authenticated", "phase2-session-current"],
} if {
  valid_actor
  not sensitive_action
}

decision := {
  "allow": true,
  "reason": "The identity provider confirmed MFA for the sensitive operation.",
  "rules": ["phase2-authenticated", "phase2-session-current", "phase2-mfa"],
} if {
  valid_actor
  sensitive_action
  input.actor.mfaSatisfied == true
}

valid_actor if {
  input.actor.principalId != "00000000-0000-0000-0000-000000000000"
  input.actor.sessionVersion >= 1
  count(input.actor.roles) > 0
}

sensitive_action if {
  input.action in {
    "ManageOrganization",
    "ManageMemberships",
    "ManageServiceAccounts",
    "PublishConfiguration",
    "CreateBackup",
    "RestoreBackup",
  }
}
