import type { components } from '@/api/schema'

/**
 * A board's types (docs/boards-plan.md §6.2, §12.2), from the API's: what both the board app and the admin's preview draw. Nothing here
 * may import the admin's client, stores, router or views (the lint rule of §4.3).
 */
type S = components['schemas']

export type BoardView = S['BoardView']
export type BoardLayout = S['BoardLayout']
export type BoardLook = S['BoardLook']
export type BoardHeader = S['BoardHeader']
export type BoardBar = S['BoardBar']
export type BoardBarView = S['BoardBarView']
export type BoardRule = S['BoardRule']
export type BoardRuleFilter = S['BoardRuleFilter']
export type BoardColumn = S['BoardColumn']
export type BoardElement = S['BoardElement']
export type BoardElementType = S['BoardElementType']
export type BoardElementSettings = S['BoardElementSettings']
export type BoardElementData = S['BoardElementData']
export type BoardColour = S['BoardColour']
export type BoardMetric = S['BoardMetric']
export type BoardPeople = S['BoardPeople']
export type BoardTimeFormat = S['BoardTimeFormat']
export type BoardDateFormat = S['BoardDateFormat']
export type BoardDateLanguage = S['BoardDateLanguage']
export type BoardTextSize = S['BoardTextSize']
export type BoardStateColour = S['BoardStateColour']
export type BoardTicket = S['BoardTicket']
export type BoardProblem = S['BoardProblem']
export type BoardLoungeLine = S['BoardLoungeLine']
export type BoardTarget = S['BoardTarget']
export type BoardRedmineTile = S['BoardRedmineTile']
export type BoardRedmineList = S['BoardRedmineList']
export type Importance = S['Importance']
