// Markdown Template Library
export interface MarkdownTemplate {
  id: string
  name: string
  description: string
  category: string
  template: string
  icon: string
}

export const MARKDOWN_TEMPLATES: MarkdownTemplate[] = [
  // Code Documentation
  {
    id: 'api-documentation',
    name: 'API Documentation',
    description: 'Complete API endpoint documentation with examples',
    category: 'Code Documentation',
    icon: '📚',
    template: `# API Endpoint Documentation

## Endpoint
\`\`\`
{{METHOD}} {{ENDPOINT_PATH}}
\`\`\`

## Description
{{DESCRIPTION}}

## Authentication
- **Required**: {{REQUIRED}}
- **Type**: {{AUTH_TYPE}}

## Request Headers
| Header | Type | Required | Description |
|--------|------|----------|-------------|
| {{HEADER_NAME}} | {{HEADER_TYPE}} | {{REQUIRED}} | {{HEADER_DESC}} |

## Request Parameters
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| {{PARAM_NAME}} | {{PARAM_TYPE}} | {{REQUIRED}} | {{PARAM_DESC}} |

## Request Body
\`\`\`json
{
  "{{FIELD}}": "{{TYPE}}"
}
\`\`\`

## Response

### Success Response (200 OK)
\`\`\`json
{
  "status": "success",
  "data": {
    "{{FIELD}}": "{{VALUE}}"
  }
}
\`\`\`

### Error Response (4xx/5xx)
\`\`\`json
{
  "status": "error",
  "message": "{{ERROR_MESSAGE}}"
}
\`\`\`

## Example Usage

### cURL
\`\`\`bash
curl -X {{METHOD}} \\
  {{BASE_URL}}{{ENDPOINT_PATH}} \\
  -H "Authorization: Bearer {{TOKEN}}" \\
  -H "Content-Type: application/json" \\
  -d '{{REQUEST_BODY}}'
\`\`\`

### JavaScript/Node.js
\`\`\`javascript
const response = await fetch('{{BASE_URL}}{{ENDPOINT_PATH}}', {
  method: '{{METHOD}}',
  headers: {
    'Authorization': 'Bearer {{TOKEN}}',
    'Content-Type': 'application/json'
  },
  body: JSON.stringify({{REQUEST_BODY}})
});

const data = await response.json();
console.log(data);
\`\`\`

### Python
\`\`\`python
import requests

response = requests.{{METHOD_LOWER}}(
    '{{BASE_URL}}{{ENDPOINT_PATH}}',
    headers={
        'Authorization': f'Bearer {{TOKEN}}',
        'Content-Type': 'application/json'
    },
    json={{REQUEST_BODY}}
)

data = response.json()
print(data)
\`\`\`

## Notes
- {{ADDITIONAL_NOTES}}
`
  },
  {
    id: 'function-documentation',
    name: 'Function Documentation',
    description: 'Comprehensive function/method documentation',
    category: 'Code Documentation',
    icon: '⚙️',
    template: `# Function Documentation

## \`{{FUNCTION_NAME}}\`

### Description
{{DESCRIPTION}}

### Signature
\`\`\`{{LANGUAGE}}
{{FUNCTION_SIGNATURE}}
\`\`\`

### Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| {{PARAM_NAME}} | {{PARAM_TYPE}} | {{REQUIRED}} | {{DEFAULT_VALUE}} | {{PARAM_DESC}} |

### Returns
- **Type**: \`{{RETURN_TYPE}}\`
- **Description**: {{RETURN_DESC}}

### Throws/Raises
| Exception | Condition |
|-----------|-----------|
| {{EXCEPTION_TYPE}} | {{CONDITION}} |

### Example Usage

#### Basic Usage
\`\`\`{{LANGUAGE}}
{{BASIC_EXAMPLE}}
\`\`\`

#### Advanced Usage
\`\`\`{{LANGUAGE}}
{{ADVANCED_EXAMPLE}}
\`\`\`

### Performance Considerations
- **Time Complexity**: {{TIME_COMPLEXITY}}
- **Space Complexity**: {{SPACE_COMPLEXITY}}
- **Notes**: {{PERFORMANCE_NOTES}}

### Related Functions
- [\`{{RELATED_FUNCTION}}\`](#{{LINK}})

### Version History
- **{{VERSION}}** ({{DATE}}): {{CHANGES}}
`
  },

  // Technical Writing
  {
    id: 'tutorial-guide',
    name: 'Tutorial Guide',
    description: 'Step-by-step tutorial with code examples',
    category: 'Technical Writing',
    icon: '🎓',
    template: `# {{TUTORIAL_TITLE}}

> **Level**: {{LEVEL}}  
> **Duration**: {{DURATION}}  
> **Prerequisites**: {{PREREQUISITES}}

## What You'll Learn
- {{LEARNING_OBJECTIVE_1}}
- {{LEARNING_OBJECTIVE_2}}
- {{LEARNING_OBJECTIVE_3}}

## Prerequisites
Before starting this tutorial, you should have:
- {{PREREQUISITE_1}}
- {{PREREQUISITE_2}}

## Step 1: {{STEP_1_TITLE}}

{{STEP_1_DESCRIPTION}}

\`\`\`{{LANGUAGE}}
{{STEP_1_CODE}}
\`\`\`

**Expected Output:**
\`\`\`
{{STEP_1_OUTPUT}}
\`\`\`

## Step 2: {{STEP_2_TITLE}}

{{STEP_2_DESCRIPTION}}

\`\`\`{{LANGUAGE}}
{{STEP_2_CODE}}
\`\`\`

> 💡 **Tip**: {{HELPFUL_TIP}}

## Step 3: {{STEP_3_TITLE}}

{{STEP_3_DESCRIPTION}}

\`\`\`{{LANGUAGE}}
{{STEP_3_CODE}}
\`\`\`

> ⚠️ **Common Pitfall**: {{COMMON_MISTAKE}}

## Testing Your Implementation

\`\`\`{{LANGUAGE}}
{{TEST_CODE}}
\`\`\`

## Troubleshooting

### {{PROBLEM_1}}
**Symptoms**: {{SYMPTOMS_1}}  
**Solution**: {{SOLUTION_1}}

### {{PROBLEM_2}}
**Symptoms**: {{SYMPTOMS_2}}  
**Solution**: {{SOLUTION_2}}

## Next Steps
- {{NEXT_STEP_1}}
- {{NEXT_STEP_2}}

## Additional Resources
- [{{RESOURCE_NAME}}]({{RESOURCE_URL}})
`
  },

  // Project Management
  {
    id: 'project-proposal',
    name: 'Project Proposal',
    description: 'Professional project proposal document',
    category: 'Business',
    icon: '📋',
    template: `# Project Proposal: {{PROJECT_NAME}}

**Date**: {{DATE}}  
**Prepared by**: {{AUTHOR}}  
**Version**: {{VERSION}}

## Executive Summary
{{EXECUTIVE_SUMMARY}}

## Problem Statement
{{PROBLEM_STATEMENT}}

## Proposed Solution
{{SOLUTION_OVERVIEW}}

### Key Features
- {{FEATURE_1}}
- {{FEATURE_2}}
- {{FEATURE_3}}

## Objectives
| Objective | Success Metric | Target Date |
|-----------|----------------|-------------|
| {{OBJECTIVE_1}} | {{METRIC_1}} | {{DATE_1}} |
| {{OBJECTIVE_2}} | {{METRIC_2}} | {{DATE_2}} |

## Scope

### In Scope
- {{IN_SCOPE_1}}
- {{IN_SCOPE_2}}

### Out of Scope
- {{OUT_OF_SCOPE_1}}
- {{OUT_OF_SCOPE_2}}

## Timeline

| Phase | Duration | Deliverables | Status |
|-------|----------|--------------|--------|
| {{PHASE_1}} | {{DURATION_1}} | {{DELIVERABLES_1}} | {{STATUS_1}} |
| {{PHASE_2}} | {{DURATION_2}} | {{DELIVERABLES_2}} | {{STATUS_2}} |

## Resources Required

### Team
- **{{ROLE_1}}**: {{TEAM_MEMBER_1}}
- **{{ROLE_2}}**: {{TEAM_MEMBER_2}}

### Budget
| Category | Estimated Cost |
|----------|----------------|
| {{CATEGORY_1}} | {{COST_1}} |
| {{CATEGORY_2}} | {{COST_2}} |
| **Total** | **{{TOTAL_COST}}** |

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|------|-------------|--------|---------------------|
| {{RISK_1}} | {{PROB_1}} | {{IMPACT_1}} | {{MITIGATION_1}} |
| {{RISK_2}} | {{PROB_2}} | {{IMPACT_2}} | {{MITIGATION_2}} |

## Success Criteria
- {{CRITERIA_1}}
- {{CRITERIA_2}}
- {{CRITERIA_3}}

## Approval
**Project Sponsor**: {{SPONSOR}}  
**Approved by**: _______________  
**Date**: _______________
`
  },

  // Meeting Notes
  {
    id: 'meeting-notes',
    name: 'Meeting Notes',
    description: 'Professional meeting minutes template',
    category: 'Business',
    icon: '📝',
    template: `# Meeting Notes: {{MEETING_TITLE}}

**Date**: {{DATE}}  
**Time**: {{TIME}}  
**Location**: {{LOCATION}}  
**Meeting Type**: {{MEETING_TYPE}}

## Attendees
- {{ATTENDEE_1}} ({{ROLE_1}})
- {{ATTENDEE_2}} ({{ROLE_2}})

## Agenda
1. {{AGENDA_ITEM_1}}
2. {{AGENDA_ITEM_2}}
3. {{AGENDA_ITEM_3}}

---

## Discussion Points

### {{TOPIC_1}}
**Discussed by**: {{SPEAKER_1}}

{{DISCUSSION_1}}

**Key Takeaways**:
- {{TAKEAWAY_1}}
- {{TAKEAWAY_2}}

### {{TOPIC_2}}
**Discussed by**: {{SPEAKER_2}}

{{DISCUSSION_2}}

**Key Takeaways**:
- {{TAKEAWAY_3}}
- {{TAKEAWAY_4}}

## Action Items

| Action | Owner | Due Date | Status |
|--------|-------|----------|--------|
| {{ACTION_1}} | {{OWNER_1}} | {{DUE_DATE_1}} | {{STATUS_1}} |
| {{ACTION_2}} | {{OWNER_2}} | {{DUE_DATE_2}} | {{STATUS_2}} |

## Decisions Made
- {{DECISION_1}}
- {{DECISION_2}}

## Parking Lot (Topics for Future Meetings)
- {{PARKING_LOT_1}}
- {{PARKING_LOT_2}}

## Next  Meeting
**Date**: {{NEXT_MEETING_DATE}}  
**Time**: {{NEXT_MEETING_TIME}}  
**Agenda**: {{NEXT_AGENDA}}

---

**Notes compiled by**: {{NOTE_TAKER}}  
**Distributed to**: {{DISTRIBUTION_LIST}}
`
  },

  // Technical Specs
  {
    id: 'technical-spec',
    name: 'Technical Specification',
    description: 'Detailed technical design document',
    category: 'Engineering',
    icon: '🔧',
    template: `# Technical Specification: {{FEATURE_NAME}}

**Author**: {{AUTHOR}}  
**Date**: {{DATE}}  
**Status**: {{STATUS}}  
**Version**: {{VERSION}}

## Overview
{{OVERVIEW}}

## Goals
- {{GOAL_1}}
- {{GOAL_2}}
- {{GOAL_3}}

## Non-Goals
- {{NON_GOAL_1}}
- {{NON_GOAL_2}}

## Background
{{BACKGROUND_CONTEXT}}

## High-Level Design

### Architecture Diagram
\`\`\`
{{ASCII_DIAGRAM}}
\`\`\`

### Components
| Component | Responsibility | Technology |
|-----------|----------------|------------|
| {{COMPONENT_1}} | {{RESPONSIBILITY_1}} | {{TECH_1}} |
| {{COMPONENT_2}} | {{RESPONSIBILITY_2}} | {{TECH_2}} |

## Detailed Design

### Data Models
\`\`\`{{LANGUAGE}}
{{DATA_MODEL_CODE}}
\`\`\`

### API Contracts

#### {{API_NAME}}
\`\`\`
{{API_SIGNATURE}}
\`\`\`

**Description**: {{API_DESCRIPTION}}

### Database Schema
\`\`\`sql
{{DATABASE_SCHEMA}}
\`\`\`

### Algorithms & Logic
{{ALGORITHM_DESCRIPTION}}

\`\`\`{{LANGUAGE}}
{{ALGORITHM_PSEUDOCODE}}
\`\`\`

**Time Complexity**: {{TIME_COMPLEXITY}}  
**Space Complexity**: {{SPACE_COMPLEXITY}}

## Security Considerations
- {{SECURITY_1}}
- {{SECURITY_2}}

## Performance Considerations
- **Expected Load**: {{EXPECTED_LOAD}}
- **Target Latency**: {{TARGET_LATENCY}}
- **Scalability**: {{SCALABILITY_NOTES}}

## Testing Strategy
- **Unit Tests**: {{UNIT_TEST_APPROACH}}
- **Integration Tests**: {{INTEGRATION_TEST_APPROACH}}
- **Load Tests**: {{LOAD_TEST_APPROACH}}

## Deployment Plan
1. {{DEPLOYMENT_STEP_1}}
2. {{DEPLOYMENT_STEP_2}}
3. {{DEPLOYMENT_STEP_3}}

## Monitoring & Alerts
| Metric | Threshold | Alert |
|--------|-----------|-------|
| {{METRIC_1}} | {{THRESHOLD_1}} | {{ALERT_1}} |
| {{METRIC_2}} | {{THRESHOLD_2}} | {{ALERT_2}} |

## Rollback Plan
{{ROLLBACK_STRATEGY}}

## Open Questions
- {{QUESTION_1}}
- {{QUESTION_2}}

## References
- [{{REFERENCE_1}}]({{URL_1}})
- [{{REFERENCE_2}}]({{URL_2}})
`
  },

  // README
  {
    id: 'readme',
    name: 'README',
    description: 'Professional project README',
    category: 'Documentation',
    icon: '📄',
    template: `# {{PROJECT_NAME}}

![Version](https://img.shields.io/badge/version-{{VERSION}}-blue.svg)
![License](https://img.shields.io/badge/license-{{LICENSE}}-green.svg)

{{PROJECT_DESCRIPTION}}

## ✨ Features

- {{FEATURE_1}}
- {{FEATURE_2}}
- {{FEATURE_3}}

## 🚀 Quick Start

### Prerequisites
- {{PREREQUISITE_1}}
- {{PREREQUISITE_2}}

### Installation

\`\`\`bash
{{INSTALLATION_COMMAND}}
\`\`\`

### Usage

\`\`\`{{LANGUAGE}}
{{USAGE_EXAMPLE}}
\`\`\`

## 📖 Documentation

- [Getting Started]({{GETTING_STARTED_URL}})
- [API Reference]({{API_REFERENCE_URL}})
- [Examples]({{EXAMPLES_URL}})

## 🛠️ Development

### Setup Development Environment

\`\`\`bash
{{DEV_SETUP_COMMANDS}}
\`\`\`

### Running Tests

\`\`\`bash
{{TEST_COMMAND}}
\`\`\`

### Building

\`\`\`bash
{{BUILD_COMMAND}}
\`\`\`

## 🤝 Contributing

Contributions are welcome! Please read our [Contributing Guide](CONTRIBUTING.md).

1. Fork the repository
2. Create your feature branch (\`git checkout -b feature/amazing-feature\`)
3. Commit your changes (\`git commit -m 'Add amazing feature'\`)
4. Push to the branch (\`git push origin feature/amazing-feature\`)
5. Open a Pull Request

## 📝 License

This project is licensed under the {{LICENSE}} License - see the [LICENSE](LICENSE) file for details.

## 👥 Authors

- **{{AUTHOR_NAME}}** - [{{AUTHOR_PROFILE}}]({{PROFILE_URL}})

## 🙏 Acknowledgments

- {{ACKNOWLEDGMENT_1}}
- {{ACKNOWLEDGMENT_2}}

## 📧 Contact

{{PROJECT_NAME}} - {{CONTACT_EMAIL}}

Project Link: [{{REPO_URL}}]({{REPO_URL}})
`
  }
]

export const getTemplatesByCategory = () => {
  const categories: Record<string, MarkdownTemplate[]> = {}
  MARKDOWN_TEMPLATES.forEach(template => {
    if (!categories[template.category]) {
      categories[template.category] = []
    }
    categories[template.category].push(template)
  })
  return categories
}

export const getTemplateById = (id: string): MarkdownTemplate | undefined => {
  return MARKDOWN_TEMPLATES.find(t => t.id === id)
}
