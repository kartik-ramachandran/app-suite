// Typed client for the Enhanced MMD (EMMD) endpoints.
import api from './api'

export interface EmmdSealStatus {
  sealed: boolean
  valid: boolean
  tampered: boolean
  sealedBy?: string
  sealedAt?: string
  reason?: string
}

export interface EmmdDocSummary {
  id: string
  title: string
  theme: string
  sealed: boolean
  mine: boolean
  organizationId?: string | null
  version: number
  createdAt: string
  updatedAt: string
}

export interface EmmdDoc {
  id: string
  title: string
  theme: string
  content: string
  mine: boolean
  canEdit: boolean
  organizationId?: string | null
  version: number
  sealToken?: string | null
  seal: EmmdSealStatus
  createdAt: string
  updatedAt: string
}

export interface SaveEmmdBody {
  title: string
  theme: string
  content: string
  /** On update: the version the client last loaded, for optimistic concurrency. */
  expectedVersion?: number
}

export async function listEmmd(): Promise<EmmdDocSummary[]> {
  const { data } = await api.get('/emmd/documents')
  return data
}

export async function getEmmd(id: string): Promise<EmmdDoc> {
  const { data } = await api.get(`/emmd/documents/${id}`)
  return data
}

export async function createEmmd(body: SaveEmmdBody): Promise<EmmdDoc> {
  const { data } = await api.post('/emmd/documents', body)
  return data
}

export async function updateEmmd(id: string, body: SaveEmmdBody): Promise<EmmdDoc> {
  const { data } = await api.put(`/emmd/documents/${id}`, body)
  return data
}

export async function deleteEmmd(id: string): Promise<void> {
  await api.delete(`/emmd/documents/${id}`)
}

export async function sealEmmd(id: string): Promise<EmmdDoc> {
  const { data } = await api.post(`/emmd/documents/${id}/seal`)
  return data
}

export async function unsealEmmd(id: string): Promise<EmmdDoc> {
  const { data } = await api.post(`/emmd/documents/${id}/unseal`)
  return data
}

export async function verifyEmmd(content: string, sealToken: string): Promise<EmmdSealStatus> {
  const { data } = await api.post('/emmd/verify', { content, sealToken })
  return data
}
